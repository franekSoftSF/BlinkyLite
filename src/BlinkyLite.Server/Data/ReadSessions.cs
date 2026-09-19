using FluentNHibernate.Cfg;
using FluentNHibernate.Cfg.Db;
using NHibernate;
using NHibernate.Cfg;
using ISession = NHibernate.ISession;
using Tool = NHibernate.Tool.hbm2ddl;

namespace BlinkyLite.Server.Data;

/// <summary>The outcome of comparing the mappings against the live schema.</summary>
public sealed record SchemaValidationResult(bool IsValid, string Summary, string? Detail = null)
{
    public override string ToString() => IsValid ? "schema ok" : $"schema drift: {Summary}";
}

/// <summary>NHibernate for reading. Writing is <see cref="Procedures"/>' job and nobody else's.</summary>
public static class ReadSessions
{
    public static Configuration BuildConfiguration(string appConnectionString) =>
        Fluently.Configure()
            .Database(PostgreSQLConfiguration.PostgreSQL83
                .ConnectionString(appConnectionString)
                .DefaultSchema(Migrations.Schema))
            .Mappings(m => m.FluentMappings.AddFromAssemblyOf<CardMap>())
            // The schema belongs to db/migrations; NHibernate must never try to
            // create or alter it, even if someone sets hbm2ddl.auto somewhere.
            .ExposeConfiguration(c => c.SetProperty(NHibernate.Cfg.Environment.Hbm2ddlAuto, "none"))
            .BuildConfiguration();

    public static ISessionFactory BuildSessionFactory(Configuration configuration) =>
        configuration.BuildSessionFactory();

    /// <summary>
    /// A session that cannot write: entities load read-only and nothing is ever
    /// flushed, so a property set by mistake goes nowhere.
    /// </summary>
    public static ISession OpenReadOnlySession(this ISessionFactory factory)
    {
        var session = factory.OpenSession();
        session.DefaultReadOnly = true;
        session.FlushMode = FlushMode.Manual;
        return session;
    }

    /// <summary>
    /// Compares the mappings with the database at start-up. Logs and lets the
    /// server run on drift: a missing column should be one readable line in a
    /// log, not a restart loop (the choice Blinky made for the same reason).
    /// </summary>
    public static SchemaValidationResult Validate(Configuration configuration)
    {
        try
        {
            new Tool.SchemaValidator(configuration).Validate();
            return new SchemaValidationResult(true, "mappings match the database");
        }
        catch (SchemaValidationException ex)
        {
            // The exception's message only says "see list"; the list is the
            // part anybody reading a log needs.
            return new SchemaValidationResult(false, string.Join("; ", ex.ValidationErrors), ex.ToString());
        }
        catch (Exception ex)
        {
            return new SchemaValidationResult(false, ex.Message.ReplaceLineEndings(" "), ex.ToString());
        }
    }
}

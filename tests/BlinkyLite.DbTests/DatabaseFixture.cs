using System.Security.Cryptography;
using BlinkyLite.Server.Data;
using NHibernate;
using Npgsql;

namespace BlinkyLite.DbTests;

[CollectionDefinition(Name)]
public sealed class DatabaseCollection : ICollectionFixture<DatabaseFixture>
{
    public const string Name = "database";
}

/// <summary>
/// A throw-away database per test run, set up exactly as an installation is:
/// db/init/00_roles.sql as a superuser, then the migrations as
/// blinkylite_owner, then everything else as blinkylite_app. Tests share it and
/// stay out of each other's way by using their own card serials.
/// </summary>
/// <remarks>
/// Roles are cluster-wide, and this fixture sets their passwords; that is why
/// every database test sits in one collection and runs serially.
/// </remarks>
public sealed class DatabaseFixture : IAsyncLifetime
{
    private readonly List<string> databases = [];
    private readonly string ownerPassword = Secret();
    private readonly string appPassword = Secret();
    private readonly string readonlyPassword = Secret();
    private long nextSerial = RandomNumberGenerator.GetInt32(1_000_000, 1_000_000_000);

    public string Superuser { get; private set; } = "";
    public string Owner { get; private set; } = "";
    public string App { get; private set; } = "";
    public string Readonly { get; private set; } = "";
    public NpgsqlDataSource AppDataSource { get; private set; } = null!;
    public Procedures Procedures { get; private set; } = null!;
    public NHibernate.Cfg.Configuration ReadConfiguration { get; private set; } = null!;
    public ISessionFactory Sessions { get; private set; } = null!;

    public long NewSerial() => Interlocked.Increment(ref nextSerial);

    public async Task InitializeAsync()
    {
        if (DbFactAttribute.ConnectionString is null)
        {
            return;
        }

        var database = await CreateDatabaseAsync();
        (Superuser, Owner, App, Readonly) = database;

        await using (var owner = NpgsqlDataSource.Create(Owner))
        {
            await Migrations.ApplyAsync(owner, _ => { });
        }

        AppDataSource = NpgsqlDataSource.Create(App);
        Procedures = new Procedures(AppDataSource);
        ReadConfiguration = ReadSessions.BuildConfiguration(App);
        Sessions = ReadSessions.BuildSessionFactory(ReadConfiguration);
    }

    /// <summary>A database with roles set up and nothing migrated.</summary>
    public async Task<(string Superuser, string Owner, string App, string Readonly)> CreateDatabaseAsync()
    {
        var name = $"blinkylite_t_{Guid.NewGuid():N}";
        databases.Add(name);

        var admin = DbFactAttribute.ConnectionString!;
        await using (var connection = new NpgsqlConnection(admin))
        {
            await connection.OpenAsync();
            await Execute(connection, $"create database {name}");
        }

        var superuser = With(admin, name, user: null, password: null);
        await using (var connection = new NpgsqlConnection(superuser))
        {
            await connection.OpenAsync();
            await Execute(connection, await File.ReadAllTextAsync(Path.Combine(Repository.Root, "db", "init", "00_roles.sql")));
            await Execute(connection, $"alter role blinkylite_owner password '{ownerPassword}'");
            await Execute(connection, $"alter role blinkylite_app password '{appPassword}'");
            await Execute(connection, $"alter role blinkylite_readonly password '{readonlyPassword}'");
        }

        return (superuser,
            With(admin, name, "blinkylite_owner", ownerPassword),
            With(admin, name, "blinkylite_app", appPassword),
            With(admin, name, "blinkylite_readonly", readonlyPassword));
    }

    public async Task DisposeAsync()
    {
        if (DbFactAttribute.ConnectionString is null)
        {
            return;
        }

        Sessions?.Dispose();
        if (AppDataSource is not null)
        {
            await AppDataSource.DisposeAsync();
        }

        NpgsqlConnection.ClearAllPools();
        await using var connection = new NpgsqlConnection(DbFactAttribute.ConnectionString);
        await connection.OpenAsync();
        foreach (var name in databases)
        {
            await Execute(connection, $"drop database if exists {name} with (force)");
        }
    }

    private static string With(string connectionString, string database, string? user, string? password)
    {
        var builder = new NpgsqlConnectionStringBuilder(connectionString) { Database = database };
        if (user is not null)
        {
            builder.Username = user;
            builder.Password = password;
        }

        return builder.ConnectionString;
    }

    private static async Task Execute(NpgsqlConnection connection, string sql)
    {
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync();
    }

    private static string Secret() => Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16));
}

internal static class Repository
{
    public static string Root { get; } = Find();

    private static string Find()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "BlinkyLite.slnx")))
            {
                return dir.FullName;
            }
        }

        throw new InvalidOperationException("BlinkyLite.slnx not found above the test output directory.");
    }
}

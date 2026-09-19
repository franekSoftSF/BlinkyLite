using System.Reflection;
using BlinkyLite.Contracts;
using BlinkyLite.Server.Data;
using Npgsql;
using Serilog;

var builder = WebApplication.CreateBuilder(args);

// A no-op unless the Service Control Manager started the process, so the same
// binary runs in a Linux container, a console and the MSIX-packaged service.
builder.Host.UseWindowsService(options => options.ServiceName = "BlinkyLite");

builder.Services.AddSerilog((services, logger) => logger
    .ReadFrom.Configuration(builder.Configuration)
    .ReadFrom.Services(services)
    .Enrich.FromLogContext()
    .WriteTo.Console());

if (args.Contains("--migrate", StringComparer.Ordinal))
{
    return await MigrateAsync(builder.Configuration);
}

var appConnectionString = builder.Configuration.GetConnectionString("App");
if (!string.IsNullOrWhiteSpace(appConnectionString))
{
    builder.Services.AddSingleton(_ => NpgsqlDataSource.Create(appConnectionString));
    builder.Services.AddSingleton<Procedures>();
    builder.Services.AddSingleton(_ => ReadSessions.BuildConfiguration(appConnectionString));
    builder.Services.AddSingleton(services =>
        ReadSessions.BuildSessionFactory(services.GetRequiredService<NHibernate.Cfg.Configuration>()));
}

var app = builder.Build();

if (!string.IsNullOrWhiteSpace(appConnectionString))
{
    // Refuses to start on missing or edited migrations: serving requests
    // against a schema this build was not written for is how data gets lost.
    try
    {
        await Migrations.VerifyAsync(app.Services.GetRequiredService<NpgsqlDataSource>());
    }
    catch (MigrationException e)
    {
        app.Logger.LogCritical("Database: {Reason} The server will not start.", e.Message);
        return 3;
    }

    var validation = ReadSessions.Validate(app.Services.GetRequiredService<NHibernate.Cfg.Configuration>());
    if (validation.IsValid)
    {
        app.Logger.LogInformation("Database: {Validation}", validation);
    }
    else
    {
        app.Logger.LogError("Database: {Validation}. {Detail}", validation, validation.Detail);
    }
}
else
{
    app.Logger.LogWarning("ConnectionStrings:App is not set; running without a database.");
}

app.UseSerilogRequestLogging();

var version = typeof(Program).Assembly
    .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "unknown";

app.MapGet("/health", () => Results.Ok(new HealthResponse("ok", version)));

await app.RunAsync();
return 0;

static async Task<int> MigrateAsync(IConfiguration configuration)
{
    var owner = configuration.GetConnectionString("Owner");
    if (string.IsNullOrWhiteSpace(owner))
    {
        Console.Error.WriteLine("--migrate needs ConnectionStrings:Owner (role blinkylite_owner).");
        return 2;
    }

    try
    {
        await using var dataSource = NpgsqlDataSource.Create(owner);
        var applied = await Migrations.ApplyAsync(dataSource, Console.WriteLine);
        Console.WriteLine(applied == 0 ? "database is up to date" : $"applied {applied} migration(s)");
        return 0;
    }
    catch (MigrationException e)
    {
        Console.Error.WriteLine(e.Message);
        return 1;
    }
}

// WebApplicationFactory in the tests needs a type to hang the entry point on.
public partial class Program;

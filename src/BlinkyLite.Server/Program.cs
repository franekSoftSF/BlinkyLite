using System.Reflection;
using BlinkyLite.Server.Api;
using BlinkyLite.Server.Data;
using BlinkyLite.Server.Secrets;
using BlinkyLite.Server.Startup;
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

if (Array.IndexOf(args, "--protect-secret") is var flag and >= 0)
{
    if (flag + 1 >= args.Length)
    {
        Console.Error.WriteLine("--protect-secret needs a name, e.g. jwt-signing-key. The value is read from standard input.");
        return ServerSetup.ExitConfiguration;
    }

    // Honours Secrets:Directory, so that the file lands where the server
    // will look for it rather than in the default location.
    return ServerSecrets.Protect(
        args[flag + 1],
        Console.In.ReadToEnd().Trim(),
        builder.Configuration.GetSection(SecretOptions.Section)["Directory"]);
}

// Secrets come from files or environment variables, never from the
// configuration itself (D-18). Laid on top, so everything downstream reads
// them the usual way.
var relaxedConfiguration = builder.Environment.IsDevelopment() || builder.Environment.IsEnvironment("Testing");
using (var loggers = LoggerFactory.Create(logging => logging.AddSimpleConsole()))
{
    try
    {
        var store = ServerSecrets.Store(builder.Configuration, loggers);
        builder.Configuration.AddInMemoryCollection(ServerSecrets.Resolve(builder.Configuration, store, relaxedConfiguration));
    }
    catch (SecretConfigurationException e)
    {
        loggers.CreateLogger("BlinkyLite").LogCritical("{Reason} The server will not start.", e.Message);
        return ServerSetup.ExitConfiguration;
    }
}

// After the secrets are in place: --migrate needs the owner password from the
// secret store too, and reading it before this point is how the first compose
// run failed with "no password has been provided".
if (args.Contains("--migrate", StringComparer.Ordinal))
{
    return await MigrateAsync(builder.Configuration);
}

builder.Services.AddBlinkyLiteProblems();
builder.Services.AddBlinkyLiteAuth(builder.Configuration);
builder.Services.AddBlinkyLiteIssuance(builder.Configuration);

var appConnectionString = builder.Configuration.GetConnectionString("App");
if (!string.IsNullOrWhiteSpace(appConnectionString))
{
    builder.Services.AddBlinkyLiteDatabase(appConnectionString);
}

var app = builder.Build();
var relaxed = relaxedConfiguration;

if (!relaxed && !ServerSetup.HasHttpsEndpoint(app.Configuration))
{
    app.Logger.LogCritical(
        "No HTTPS endpoint is configured. The operator's AD password and the JWT travel on this connection, " +
        "so the server refuses to start. Configure Kestrel:Endpoints:Https or ASPNETCORE_URLS.");
    return ServerSetup.ExitNoTls;
}

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
        return ServerSetup.ExitSchema;
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
else if (relaxed)
{
    app.Logger.LogWarning("ConnectionStrings:App is not set; running without a database.");
}
else
{
    app.Logger.LogCritical("ConnectionStrings:App is not set; without it nothing can be audited.");
    return ServerSetup.ExitConfiguration;
}

// First, so every later piece - the audit's source address, the per-address
// login limit - sees the operator's address and not nginx's (0055).
app.UseBlinkyLiteForwardedHeaders(builder.Configuration);
app.UseBlinkyLiteExceptions();
app.UseStatusCodePages();
app.UseSerilogRequestLogging();
app.UseRateLimiter();
app.UseAuthentication();
app.UseAuthorization();

var version = typeof(Program).Assembly
    .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "unknown";

app.MapBlinkyLiteApi(version);
app.MapIssuanceApi();
app.MapBrowseApi();

await app.RunAsync();
return 0;

static async Task<int> MigrateAsync(IConfiguration configuration)
{
    var owner = configuration.GetConnectionString("Owner");
    if (string.IsNullOrWhiteSpace(owner))
    {
        Console.Error.WriteLine("--migrate needs ConnectionStrings:Owner (role blinkylite_owner).");
        return ServerSetup.ExitConfiguration;
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

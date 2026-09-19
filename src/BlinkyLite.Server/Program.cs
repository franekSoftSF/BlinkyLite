using System.Reflection;
using BlinkyLite.Contracts;
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

var app = builder.Build();

app.UseSerilogRequestLogging();

var version = typeof(Program).Assembly
    .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "unknown";

app.MapGet("/health", () => Results.Ok(new HealthResponse("ok", version)));

app.Run();

// WebApplicationFactory in the tests needs a type to hang the entry point on.
public partial class Program;

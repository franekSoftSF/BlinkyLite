using System.Threading.RateLimiting;
using BlinkyLite.Contracts;
using BlinkyLite.Server.Api;
using BlinkyLite.Server.Auth;
using BlinkyLite.Server.Data;
using BlinkyLite.Server.Secrets;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.RateLimiting;
using Npgsql;

namespace BlinkyLite.Server.Startup;

/// <summary>Wiring, kept out of Program.cs so that the startup order stays readable.</summary>
public static class ServerSetup
{
    /// <summary>Exit codes, so that a service manager or a container can tell what went wrong.</summary>
    public const int ExitConfiguration = 2;
    public const int ExitSchema = 3;
    public const int ExitNoTls = 4;

    public static void AddBlinkyLiteAuth(this IServiceCollection services, IConfiguration configuration)
    {
        var jwt = configuration.GetSection(JwtOptions.Section).Get<JwtOptions>() ?? new JwtOptions();

        // Key() throws on a missing or short key: better at start-up than at
        // the first sign-in attempt.
        jwt.Key();

        services.AddSingleton(jwt);
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton<TokenService>();
        services.AddSingleton<FailedLogins>();
        services.AddSingleton(RoleMap.From(configuration));
        services.AddScoped<LoginService>();

        var ldap = configuration.GetSection(LdapOptions.Section).Get<LdapOptions>();
        if (ldap is not null && !string.IsNullOrWhiteSpace(ldap.Server))
        {
            services.AddSingleton(ldap);
            services.AddSingleton<IDirectory, LdapDirectory>();
        }
        else
        {
            // Without this a sign-in attempt would fail while resolving
            // services and read as a server bug rather than as "not set up".
            services.AddSingleton<IDirectory, UnconfiguredDirectory>();
        }

        var keks = configuration.GetSection(KekOptions.Section).Get<KekOptions>();
        if (keks is not null && keks.Keks.Count > 0)
        {
            services.AddSingleton(new SecretEnvelopes(keks));
        }

        services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
            .AddJwtBearer(options =>
            {
                // Without this, "sub" is rewritten to the long WS-Federation
                // claim name and every lookup of it quietly returns nothing.
                options.MapInboundClaims = false;
                options.TokenValidationParameters = TokenService.ValidationParameters(jwt);
            });

        services.AddAuthorizationBuilder()
            .SetFallbackPolicy(new Microsoft.AspNetCore.Authorization.AuthorizationPolicyBuilder()
                .RequireAuthenticatedUser().Build());

        services.AddAuthorization(options =>
        {
            foreach (var (policy, roles) in Policies.Roles)
            {
                options.AddPolicy(policy, builder => builder
                    .RequireAuthenticatedUser()
                    .RequireRole(roles.Select(r => r.ToString())));
            }
        });

        services.AddRateLimiter(options =>
        {
            options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            options.AddPolicy(Endpoints.LoginRateLimit, context => RateLimitPartition.GetFixedWindowLimiter(
                context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
                _ => new FixedWindowRateLimiterOptions
                {
                    PermitLimit = configuration.GetValue("RateLimits:LoginPerMinute", 10),
                    Window = TimeSpan.FromMinutes(1),
                    QueueLimit = 0,
                }));
        });
    }

    public static void AddBlinkyLiteDatabase(this IServiceCollection services, string connectionString)
    {
        services.AddSingleton(_ => NpgsqlDataSource.Create(connectionString));
        services.AddSingleton<IProcedures, Procedures>();
        services.AddSingleton(_ => ReadSessions.BuildConfiguration(connectionString));
        services.AddSingleton(provider =>
            ReadSessions.BuildSessionFactory(provider.GetRequiredService<NHibernate.Cfg.Configuration>()));
    }

    /// <summary>
    /// Refuses to serve over plain HTTP outside development: the operator's AD
    /// password and the JWT travel on this connection. Termination elsewhere is
    /// not an option - the server is the only thing in front of itself
    /// (docs/01-architecture.md).
    /// </summary>
    public static bool HasHttpsEndpoint(IConfiguration configuration)
    {
        var urls = new[] { configuration["urls"], configuration["URLS"], configuration["ASPNETCORE_URLS"] }
            .Concat(configuration.GetSection("Kestrel:Endpoints").GetChildren().Select(e => e["Url"]))
            .Where(u => !string.IsNullOrWhiteSpace(u));

        return !string.IsNullOrWhiteSpace(configuration["HTTPS_PORTS"])
            || !string.IsNullOrWhiteSpace(configuration["ASPNETCORE_HTTPS_PORTS"])
            || urls.Any(u => u!.Contains("https://", StringComparison.OrdinalIgnoreCase));
    }
}

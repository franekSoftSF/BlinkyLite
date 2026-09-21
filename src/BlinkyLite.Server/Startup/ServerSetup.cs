using System.Threading.RateLimiting;
using BlinkyLite.Contracts;
using BlinkyLite.Server.Api;
using BlinkyLite.Server.Auth;
using BlinkyLite.Server.Data;
using BlinkyLite.Piv.Attestation;
using BlinkyLite.Server.Issuing;
using BlinkyLite.Server.Secrets;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.DependencyInjection.Extensions;
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

    /// <summary>
    /// The profile list, checked at start-up rather than at the first
    /// issuance: a server with a broken one looks healthy right up to the
    /// moment somebody is standing at a desk with a token in their hand.
    /// </summary>
    public static void AddBlinkyLiteIssuance(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<IssuanceOptions>()
            .Bind(configuration.GetSection(IssuanceOptions.Section))
            .Validate(options => options.Problems().Count == 0,
                string.Join(" ", configuration.GetSection(IssuanceOptions.Section).Get<IssuanceOptions>()?.Problems()
                                 ?? ["Issuance is not configured."]))
            .ValidateOnStart();

        // Registered rather than built inside the service, so a test can put
        // a synthetic Yubico PKI in its place - a real attestation names one
        // physical token and this repository is public.
        services.TryAddSingleton(AttestationVerifier.ForYubico());
        services.AddScoped<IssuanceService>();
    }

    /// <summary>
    /// Believes <c>X-Forwarded-For</c> - but only when the configuration says
    /// the server sits behind its own proxy.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The audit records the source address of every sign-in and every PUK
    /// disclosure. Behind nginx that address would be nginx's container for
    /// all of them, and the log would stop saying who came from where.
    /// </para>
    /// <para>
    /// Off by default, because a server that believes the header from anybody
    /// lets anybody write any address into the audit. On in Docker, where the
    /// server publishes no port and nginx is the only thing that can reach it
    /// (D-32) - so the one party able to send the header is the one that sets
    /// it. One hop only: the value nginx writes, not a chain a client could
    /// have started.
    /// </para>
    /// </remarks>
    public static void UseBlinkyLiteForwardedHeaders(this IApplicationBuilder app, IConfiguration configuration)
    {
        if (!configuration.GetValue("ForwardedHeaders:BehindOwnProxy", false))
        {
            return;
        }

        var options = new ForwardedHeadersOptions
        {
            ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto,
            ForwardLimit = 1,
        };

        // The proxy's address is assigned by Docker and changes between
        // deployments; reachability, not an address list, is what restricts
        // who can send the header here.
        options.KnownIPNetworks.Clear();
        options.KnownProxies.Clear();

        app.UseForwardedHeaders(options);
    }

    public static void AddBlinkyLiteDatabase(this IServiceCollection services, string connectionString)
    {
        services.AddSingleton(_ => NpgsqlDataSource.Create(connectionString));
        services.AddSingleton<IProcedures, Procedures>();
        services.AddSingleton<IIssuanceReader, IssuanceReader>();
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

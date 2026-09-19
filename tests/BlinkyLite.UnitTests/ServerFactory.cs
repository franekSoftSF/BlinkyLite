using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using BlinkyLite.Contracts;
using BlinkyLite.Server.Auth;
using BlinkyLite.Server.Data;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace BlinkyLite.UnitTests;

/// <summary>
/// The real server with AD and PostgreSQL replaced by fakes. Everything else -
/// authentication, policies, problem shapes, rate limits - is the production
/// wiring, because that is what these tests are about.
/// </summary>
public sealed class ServerFactory : WebApplicationFactory<Program>
{
    public const string AdminGroup = "S-1-5-21-100-200-300-1100";
    public const string OfficerGroup = "S-1-5-21-100-200-300-1101";
    public const string HelpdeskGroup = "S-1-5-21-100-200-300-1102";

    public FakeDirectory Directory { get; } = new();

    public RecordingProcedures Procedures { get; } = new();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.UseSetting("Jwt:SigningKey", Convert.ToBase64String(new byte[32]));
        builder.UseSetting($"Roles:{Role.Admin}:0", AdminGroup);
        builder.UseSetting($"Roles:{Role.SecurityOfficer}:0", OfficerGroup);
        builder.UseSetting($"Roles:{Role.Helpdesk}:0", HelpdeskGroup);
        builder.UseSetting("RateLimits:LoginPerMinute", "1000");

        builder.ConfigureServices(services =>
        {
            services.AddSingleton<IDirectory>(Directory);
            services.AddSingleton<IProcedures>(Procedures);
        });
    }

    public async Task<HttpClient> SignedInAs(params Role[] roles)
    {
        var groups = roles.Select(r => r switch
        {
            Role.Admin => AdminGroup,
            Role.SecurityOfficer => OfficerGroup,
            _ => HelpdeskGroup,
        }).ToArray();

        Directory.Accounts["operator"] = new DirectoryAccount(
            new DirectoryUser(@"CORP\operator", "operator@corp.example", "S-1-5-21-100-200-300-5000", "Op Erator", true),
            groups);

        var client = CreateClient();
        var response = await client.PostAsJsonAsync("/api/auth/login", new LoginRequest("operator", "right"));
        response.EnsureSuccessStatusCode();
        var login = (await response.Content.ReadFromJsonAsync<LoginResponse>())!;
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", login.Token);
        return client;
    }
}

public sealed class FakeDirectory : IDirectory
{
    public Dictionary<string, DirectoryAccount> Accounts { get; } = new(StringComparer.OrdinalIgnoreCase);

    public List<DirectoryUser> Users { get; } = [];

    public string Password { get; set; } = "right";

    public Exception? Throws { get; set; }

    public Task<DirectoryAccount?> AuthenticateAsync(string username, string password, CancellationToken ct = default)
    {
        if (Throws is not null)
        {
            throw Throws;
        }

        var found = password == Password && Accounts.TryGetValue(username, out var account) ? account : null;
        return Task.FromResult(found);
    }

    public Task<IReadOnlyList<DirectoryUser>> SearchUsersAsync(string query, int limit, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<DirectoryUser>>(
            Users.Where(u => u.DisplayName.Contains(query, StringComparison.OrdinalIgnoreCase)).Take(limit).ToList());
}

/// <summary>Records what the server would have written to the database.</summary>
public sealed class RecordingProcedures : IProcedures
{
    public List<(AuditAction Action, object? Data, Actor Actor)> Audits { get; } = [];

    public Task AuditAsync(AuditAction action, object? data, Actor actor, CancellationToken ct = default)
    {
        Audits.Add((action, data, actor));
        return Task.CompletedTask;
    }

    public string? ReasonOf(int index) =>
        Audits[index].Data?.GetType().GetProperty("reason")?.GetValue(Audits[index].Data) as string;

    public Task<Guid> ReserveIssuanceAsync(IssuanceReservation r, Actor actor, CancellationToken ct = default) => throw new NotSupportedException();

    public Task MarkCustomisedAsync(Guid issuanceId, Actor actor, CancellationToken ct = default) => throw new NotSupportedException();

    public Task MarkAttestedAsync(Guid issuanceId, AttestationRecord a, Actor actor, CancellationToken ct = default) => throw new NotSupportedException();

    public Task MarkSubmittedAsync(Guid issuanceId, int caRequestId, string eaThumbprint, Actor actor, CancellationToken ct = default) => throw new NotSupportedException();

    public Task MarkPendingAsync(Guid issuanceId, Actor actor, CancellationToken ct = default) => throw new NotSupportedException();

    public Task MarkIssuedAsync(Guid issuanceId, IssuedCertificate c, Actor actor, CancellationToken ct = default) => throw new NotSupportedException();

    public Task MarkFailedAsync(Guid issuanceId, string error, Actor actor, CancellationToken ct = default) => throw new NotSupportedException();

    public Task<SecretEnvelope> DiscloseSecretAsync(long cardSerial, SecretKind kind, string reason, Actor actor, CancellationToken ct = default) => throw new NotSupportedException();

    public Task<IReadOnlyList<ManagementKeyCandidate>> GetManagementKeyCandidatesAsync(long cardSerial, Actor actor, CancellationToken ct = default) => throw new NotSupportedException();
}

internal static class HttpAssertions
{
    /// <summary>The message key from a ProblemDetails body.</summary>
    public static async Task<string?> ProblemCode(this HttpResponseMessage response)
    {
        var problem = await response.Content.ReadFromJsonAsync<Dictionary<string, object>>();
        return problem?.TryGetValue(ErrorCodes.ProblemCodeKey, out var code) == true ? code.ToString() : null;
    }

    public static async Task ShouldBe(this HttpResponseMessage response, HttpStatusCode status, string code)
    {
        Assert.Equal(status, response.StatusCode);
        Assert.Equal(code, await response.ProblemCode());
    }
}

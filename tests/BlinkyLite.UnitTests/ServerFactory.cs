using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using BlinkyLite.Contracts;
using BlinkyLite.Piv.Attestation;
using BlinkyLite.Server.Auth;
using BlinkyLite.Server.Data;
using ServerIssuance = BlinkyLite.Server.Data.Issuance;
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

    public FakeIssuances Issuances { get; } = new();

    /// <summary>
    /// A synthetic Yubico PKI in place of the real roots, for the tests that
    /// need an attestation to verify. Null leaves the production pinning,
    /// which is what makes "this is not Yubico's" a meaningful test.
    /// </summary>
    public AttestationVerifier? Verifier { get; init; }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.UseSetting("Jwt:SigningKey", Convert.ToBase64String(new byte[32]));
        builder.UseSetting($"Roles:{Role.Admin}:0", AdminGroup);
        builder.UseSetting($"Roles:{Role.SecurityOfficer}:0", OfficerGroup);
        builder.UseSetting($"Roles:{Role.Helpdesk}:0", HelpdeskGroup);
        builder.UseSetting("RateLimits:LoginPerMinute", "1000");

        // One profile, as in the normal case (D-21). A KEK is needed because
        // the server seals the secrets before it writes the reservation.
        builder.UseSetting("Issuance:CertificationAuthority", @"SUBCA\Corp Issuing CA");
        builder.UseSetting("Issuance:Profiles:0:Name", "Karta");
        builder.UseSetting("Issuance:Profiles:0:Template", "CorpSmartcardLogon");
        builder.UseSetting("Secrets:CurrentKekVersion", "1");
        builder.UseSetting("Secrets:Keks:1", Convert.ToBase64String(Enumerable.Range(0, 32).Select(i => (byte)i).ToArray()));

        builder.ConfigureServices(services =>
        {
            services.AddSingleton<IDirectory>(Directory);
            services.AddSingleton<IProcedures>(Procedures);
            services.AddSingleton<IIssuanceReader>(Issuances);

            if (Verifier is not null)
            {
                services.AddSingleton(Verifier);
            }
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

    public Task<DirectoryUser?> FindBySidAsync(string sid, CancellationToken ct = default) =>
        Task.FromResult(Users.FirstOrDefault(u => u.Sid == sid));
}

/// <summary>Stands in for the read side; the issuances a test put there.</summary>
/// <remarks>
/// Aliased on purpose: the test project also sees the engine's namespace
/// <c>BlinkyLite.Issuance</c>, so a bare <c>Issuance</c> binds to that instead
/// of to the entity.
/// </remarks>
public sealed class FakeIssuances : IIssuanceReader
{
    public Dictionary<Guid, ServerIssuance> Rows { get; } = [];

    public ServerIssuance? Find(Guid id) => Rows.GetValueOrDefault(id);
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

    public List<IssuanceReservation> Reservations { get; } = [];

    public List<(Guid Id, AttestationRecord Record)> Attestations { get; } = [];

    public List<(Guid Id, IssuedCertificate Certificate)> Issued { get; } = [];

    public List<(Guid Id, string Step)> Steps { get; } = [];

    public Task<Guid> ReserveIssuanceAsync(IssuanceReservation r, Actor actor, CancellationToken ct = default)
    {
        Reservations.Add(r);
        return Task.FromResult(r.IssuanceId);
    }

    public Task MarkCustomisedAsync(Guid issuanceId, Actor actor, CancellationToken ct = default) =>
        Step(issuanceId, "customised");

    public Task MarkAttestedAsync(Guid issuanceId, AttestationRecord a, Actor actor, CancellationToken ct = default)
    {
        Attestations.Add((issuanceId, a));
        return Step(issuanceId, "attested");
    }

    public Task MarkSubmittedAsync(Guid issuanceId, int caRequestId, string eaThumbprint, Actor actor, CancellationToken ct = default) =>
        Step(issuanceId, "submitted");

    public Task MarkPendingAsync(Guid issuanceId, Actor actor, CancellationToken ct = default) =>
        Step(issuanceId, "pending");

    public Task MarkIssuedAsync(Guid issuanceId, IssuedCertificate c, Actor actor, CancellationToken ct = default)
    {
        Issued.Add((issuanceId, c));
        return Step(issuanceId, "issued");
    }

    public Task MarkFailedAsync(Guid issuanceId, string error, Actor actor, CancellationToken ct = default) =>
        Step(issuanceId, "failed");

    private Task Step(Guid id, string step)
    {
        Steps.Add((id, step));
        return Task.CompletedTask;
    }

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

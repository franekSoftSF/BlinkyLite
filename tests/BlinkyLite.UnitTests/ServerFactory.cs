using System.Collections.Concurrent;
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

    public const string OperatorSid = "S-1-5-21-100-200-300-5000";

    /// <summary>
    /// Signed in through both steps. The operator's second factor is set up
    /// afresh each time: signing in twice within thirty seconds with one
    /// secret is, correctly, a replayed code.
    /// </summary>
    public async Task<HttpClient> SignedInAs(params Role[] roles)
    {
        var groups = roles.Select(r => r switch
        {
            Role.Admin => AdminGroup,
            Role.SecurityOfficer => OfficerGroup,
            _ => HelpdeskGroup,
        }).ToArray();

        Directory.Accounts["operator"] = new DirectoryAccount(
            new DirectoryUser(@"CORP\operator", "operator@corp.example", OperatorSid, "Op Erator", true),
            groups);
        Procedures.Totp.TryRemove(OperatorSid, out _);

        var client = CreateClient();
        var challenge = await Challenge(client, "operator");
        var secret = await SetUp(client, challenge);
        var login = await SecondStep(client, challenge, TotpCode(secret));

        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", login.Token);
        return client;
    }

    public static async Task<LoginChallenge> Challenge(HttpClient client, string username, string password = "right")
    {
        var response = await client.PostAsJsonAsync("/api/auth/login", new LoginRequest(username, password));
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<LoginChallenge>())!;
    }

    /// <summary>Starts a setup with the ticket and returns the raw secret.</summary>
    public static async Task<byte[]> SetUp(HttpClient client, LoginChallenge challenge)
    {
        var response = await client.SendAsync(WithTicket(HttpMethod.Post, "/api/auth/totp/setup", challenge));
        response.EnsureSuccessStatusCode();
        var setup = (await response.Content.ReadFromJsonAsync<TotpSetupResponse>())!;
        return Base32Decode(setup.Secret);
    }

    public static Task<HttpResponseMessage> SendCode(HttpClient client, LoginChallenge challenge, string code)
    {
        var request = WithTicket(HttpMethod.Post, "/api/auth/totp", challenge);
        request.Content = JsonContent.Create(new SecondFactorRequest(code));
        return client.SendAsync(request);
    }

    public static async Task<LoginResponse> SecondStep(HttpClient client, LoginChallenge challenge, string code)
    {
        var response = await SendCode(client, challenge, code);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<LoginResponse>())!;
    }

    public static string TotpCode(byte[] secret, int stepOffset = 0) =>
        Totp.Code(secret, Totp.StepAt(DateTimeOffset.UtcNow) + stepOffset);

    public static HttpRequestMessage WithTicket(HttpMethod method, string path, LoginChallenge challenge)
    {
        var request = new HttpRequestMessage(method, path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", challenge.Ticket);
        return request;
    }

    /// <summary>RFC 4648 base32 back to bytes, as an authenticator app reads it.</summary>
    public static byte[] Base32Decode(string text)
    {
        const string alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";
        var output = new List<byte>();
        int buffer = 0, bits = 0;
        foreach (var c in text.TrimEnd('='))
        {
            buffer = (buffer << 5) | alphabet.IndexOf(c, StringComparison.Ordinal);
            bits += 5;
            if (bits >= 8)
            {
                output.Add((byte)(buffer >> (bits - 8)));
                bits -= 8;
            }
        }

        return [.. output];
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

    public Dictionary<long, Card> Cards { get; } = [];

    public Dictionary<Guid, CardSecret> Secrets { get; } = [];

    public List<AuditEvent> AuditRows { get; } = [];

    public ServerIssuance? Find(Guid id) => Rows.GetValueOrDefault(id);

    public Page<ServerIssuance> List(string? query, int page, int pageSize)
    {
        var rows = Rows.Values
            .Where(i => query is null || i.TargetDisplayName.Contains(query, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(i => i.CreatedAt)
            .ToList();
        return new Page<ServerIssuance>(rows.Skip((page - 1) * pageSize).Take(pageSize).ToList(), rows.Count, page, pageSize);
    }

    public Card? FindCard(long serial) => Cards.GetValueOrDefault(serial);

    public CardSecret? SecretOf(Guid issuanceId) => Secrets.GetValueOrDefault(issuanceId);

    public Page<AuditEvent> Audit(long? cardSerial, int page, int pageSize)
    {
        var rows = AuditRows.Where(a => cardSerial is null || a.CardSerial == cardSerial).OrderByDescending(a => a.Id).ToList();
        return new Page<AuditEvent>(rows.Skip((page - 1) * pageSize).Take(pageSize).ToList(), rows.Count, page, pageSize);
    }
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

    /// <summary>Operators' second factors, with the same rules as migration 0006.</summary>
    public ConcurrentDictionary<string, FakeTotp> Totp { get; } = new();

    public Task<TotpState?> GetTotpAsync(Actor actor, CancellationToken ct = default) =>
        Task.FromResult(Totp.TryGetValue(actor.Sid!, out var t) ? new TotpState(t.Envelope, t.KekVersion, t.Confirmed) : null);

    public Task BeginTotpAsync(byte[] envelope, short kekVersion, Actor actor, CancellationToken ct = default)
    {
        if (Totp.TryGetValue(actor.Sid!, out var existing) && existing.Confirmed)
        {
            throw Rule("BL007", ErrorCodes.TotpAlreadyConfigured);
        }

        Totp[actor.Sid!] = new FakeTotp(envelope, kekVersion);
        return Task.CompletedTask;
    }

    public Task ConfirmTotpAsync(long step, IReadOnlyList<byte[]> backupCodeHashes, Actor actor, CancellationToken ct = default)
    {
        var t = Totp.TryGetValue(actor.Sid!, out var found) ? found : throw Rule("BL008", ErrorCodes.TotpSetupRequired);
        lock (t)
        {
            if (t.Confirmed)
            {
                throw Rule("BL007", ErrorCodes.TotpAlreadyConfigured);
            }

            t.Confirmed = true;
            t.LastStep = step;
            t.BackupCodes.Clear();
            t.BackupCodes.AddRange(backupCodeHashes.Select(h => (Convert.ToHexString(h), false)));
        }

        Audits.Add((AuditAction.AuthLogin, new { second_factor = "totp" }, actor));
        return Task.CompletedTask;
    }

    public Task AcceptTotpAsync(long step, Actor actor, CancellationToken ct = default)
    {
        var t = Totp.TryGetValue(actor.Sid!, out var found) && found.Confirmed ? found : throw Rule("BL008", ErrorCodes.TotpSetupRequired);
        lock (t)
        {
            if (t.LastStep is { } last && step <= last)
            {
                throw Rule("BL006", ErrorCodes.TotpInvalid);
            }

            t.LastStep = step;
        }

        Audits.Add((AuditAction.AuthLogin, new { second_factor = "totp" }, actor));
        return Task.CompletedTask;
    }

    public Task<int> UseBackupCodeAsync(byte[] codeHash, Actor actor, CancellationToken ct = default)
    {
        var t = Totp.TryGetValue(actor.Sid!, out var found) && found.Confirmed ? found : throw Rule("BL008", ErrorCodes.TotpSetupRequired);
        lock (t)
        {
            var index = t.BackupCodes.FindIndex(c => c.Hash == Convert.ToHexString(codeHash) && !c.Used);
            if (index < 0)
            {
                throw Rule("BL006", ErrorCodes.TotpInvalid);
            }

            t.BackupCodes[index] = (t.BackupCodes[index].Hash, true);
            Audits.Add((AuditAction.AuthLogin, new { second_factor = "backup-code" }, actor));
            return Task.FromResult(t.BackupCodes.Count(c => !c.Used));
        }
    }

    public List<(string OperatorSid, string Reason, Actor Actor)> Resets { get; } = [];

    public Task ResetTotpAsync(string operatorSid, string reason, Actor actor, CancellationToken ct = default)
    {
        Resets.Add((operatorSid, reason, actor));
        Totp.TryRemove(operatorSid, out _);
        return Task.CompletedTask;
    }

    private static DatabaseRuleException Rule(string sqlState, string key) =>
        new(sqlState, key, null, new InvalidOperationException(key));

    /// <summary>What bl_secret_disclose would hand out, by card and kind.</summary>
    public Dictionary<(long Serial, SecretKind Kind), SecretEnvelope> Envelopes { get; } = [];

    public List<(long Serial, SecretKind Kind, string Reason, Actor Actor)> Disclosures { get; } = [];

    public Task<SecretEnvelope> DiscloseSecretAsync(long cardSerial, SecretKind kind, string reason, Actor actor, CancellationToken ct = default)
    {
        if (reason.Trim().Length < 5)
        {
            throw Rule("BL005", ErrorCodes.ReasonRequired);
        }

        if (!Envelopes.TryGetValue((cardSerial, kind), out var envelope))
        {
            throw Rule("BL002", ErrorCodes.NotFound);
        }

        Disclosures.Add((cardSerial, kind, reason, actor));
        return Task.FromResult(envelope);
    }

    public Task<IReadOnlyList<ManagementKeyCandidate>> GetManagementKeyCandidatesAsync(long cardSerial, Actor actor, CancellationToken ct = default) => throw new NotSupportedException();
}

public sealed class FakeTotp(byte[] envelope, short kekVersion)
{
    public byte[] Envelope { get; } = envelope;

    public short KekVersion { get; } = kekVersion;

    public bool Confirmed { get; set; }

    public long? LastStep { get; set; }

    public List<(string Hash, bool Used)> BackupCodes { get; } = [];
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

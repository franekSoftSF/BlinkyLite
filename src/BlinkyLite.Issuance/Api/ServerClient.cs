using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using BlinkyLite.Contracts;

namespace BlinkyLite.Issuance.Api;

/// <summary>The server refused, with the key it sent and the HTTP status.</summary>
public sealed class ServerException(HttpStatusCode status, string messageKey, string? detail)
    : Exception($"{messageKey} ({(int)status})" + (detail is null ? "" : $": {detail}"))
{
    public HttpStatusCode Status { get; } = status;

    public string MessageKey { get; } = messageKey;
}

/// <summary>
/// The station's side of the API: one method per step of docs/02.
/// </summary>
/// <remarks>
/// <para>
/// The token lives in this object and nowhere else - not on disk, not in an
/// environment variable. It expires in thirty minutes, which is longer than an
/// issuance and shorter than a working day.
/// </para>
/// <para>
/// Every failure comes back as the server's message key, not as an English
/// sentence: the server does not translate, and the shell shows the key in the
/// operator's language (docs/08).
/// </para>
/// </remarks>
public sealed class ServerClient : IDisposable
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly HttpClient http;

    public ServerClient(Uri server, HttpMessageHandler? handler = null)
    {
        http = handler is null ? new HttpClient() : new HttpClient(handler);
        http.BaseAddress = server;
        http.Timeout = TimeSpan.FromSeconds(30);
    }

    public CurrentUser? User { get; private set; }

    /// <summary>After a sign-in with a backup code: how many are left, so the shell can warn.</summary>
    public int? BackupCodesLeft { get; private set; }

    /// <summary>
    /// The password step. Returns the ticket for the code; a ticket for a
    /// setup is refused here, because only the web console can show the QR
    /// code a setup needs (0027).
    /// </summary>
    public async Task<LoginChallenge> BeginLoginAsync(string username, string password, CancellationToken ct = default)
    {
        var response = await http.PostAsJsonAsync("/api/auth/login", new LoginRequest(username, password), Json, ct);

        return Checked(await ReadAsync<LoginChallenge>(response, ct));
    }

    /// <summary>
    /// The first step with the Windows identity of whoever runs this process
    /// (0025): Kerberos through SSPI, for the SPN <c>HTTP/&lt;host of the
    /// address&gt;</c>. The code step after it is the same as after a password.
    /// </summary>
    /// <remarks>
    /// A client of its own with default credentials, for this one request:
    /// the shared client never offers the Windows identity anywhere else, so a
    /// server that asked for Negotiate on another endpoint would not get it.
    /// </remarks>
    public async Task<LoginChallenge> BeginWindowsLoginAsync(CancellationToken ct = default)
    {
        using var handler = new SocketsHttpHandler { Credentials = CredentialCache.DefaultCredentials };
        using var windows = new HttpClient(handler) { BaseAddress = http.BaseAddress, Timeout = http.Timeout };

        var response = await windows.PostAsync("/api/auth/negotiate", content: null, ct);

        // A 401 here is Windows not getting a ticket the server accepts - no
        // SPN for this name, a machine outside the domain, an IP address in
        // the URL. The server cannot tell which; it can only say it was not
        // Kerberos it received.
        if (response.StatusCode == HttpStatusCode.Unauthorized)
        {
            throw new ServerException(HttpStatusCode.Unauthorized, ErrorCodes.KerberosFailed,
                $"no Kerberos ticket for HTTP/{http.BaseAddress?.Host} was accepted");
        }

        return Checked(await ReadAsync<LoginChallenge>(response, ct));
    }

    private static LoginChallenge Checked(LoginChallenge challenge) =>
        challenge.Next == LoginNext.Totp
            ? challenge
            : throw new ServerException(HttpStatusCode.Conflict, ErrorCodes.TotpSetupRequired, $"next step {challenge.Next}");

    /// <summary>
    /// The code step. A wrong code throws <see cref="ServerException"/> with
    /// <see cref="ErrorCodes.TotpInvalid"/> and the same challenge may be tried
    /// again until it expires.
    /// </summary>
    public async Task<CurrentUser> CompleteLoginAsync(LoginChallenge challenge, string code, CancellationToken ct = default)
    {
        // The ticket goes on this one request, not on the client: it must
        // never be sent anywhere a token would be.
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/auth/totp")
        {
            Content = JsonContent.Create(new SecondFactorRequest(code), options: Json),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", challenge.Ticket);

        var login = await ReadAsync<LoginResponse>(await http.SendAsync(request, ct), ct);

        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", login.Token);
        User = login.User;
        BackupCodesLeft = login.BackupCodesLeft;

        return login.User;
    }

    /// <summary>
    /// Both steps, for a shell that can ask a question and wait: the console
    /// tool and the PowerShell module.
    /// </summary>
    /// <param name="askCode">
    /// Asked for the code; gets the message key of the previous refusal, or
    /// null the first time. Returning null gives up.
    /// </param>
    public async Task<CurrentUser> LoginAsync(
        string username, string password, Func<string?, string?> askCode, CancellationToken ct = default)
    {
        return await CodeAsync(await BeginLoginAsync(username, password, ct), askCode, ct);
    }

    /// <summary>Both steps with the Windows identity instead of a password.</summary>
    public async Task<CurrentUser> LoginWithWindowsAsync(Func<string?, string?> askCode, CancellationToken ct = default) =>
        await CodeAsync(await BeginWindowsLoginAsync(ct), askCode, ct);

    private async Task<CurrentUser> CodeAsync(LoginChallenge challenge, Func<string?, string?> askCode, CancellationToken ct)
    {
        string? refusal = null;

        // Three tries, then back to the password: the server locks the
        // account after five failures in all, and a shell should not be the
        // one that spends them.
        for (var attempt = 0; attempt < 3; attempt++)
        {
            var code = askCode(refusal);
            if (string.IsNullOrWhiteSpace(code))
            {
                throw new OperationCanceledException("No code was given.");
            }

            try
            {
                return await CompleteLoginAsync(challenge, code.Trim(), ct);
            }
            catch (ServerException e) when (e.MessageKey == ErrorCodes.TotpInvalid)
            {
                refusal = e.MessageKey;
            }
        }

        throw new ServerException(HttpStatusCode.Unauthorized, ErrorCodes.TotpInvalid, "three wrong codes");
    }

    /// <summary>What the record says about a card, or null when this server never issued it.</summary>
    public async Task<CardRecord?> CardAsync(long serial, CancellationToken ct = default)
    {
        var response = await http.GetAsync($"/api/cards/{serial}", ct);

        return response.StatusCode == HttpStatusCode.NotFound ? null : await ReadAsync<CardRecord>(response, ct);
    }

    public async Task<IReadOnlyList<IssuanceProfile>> ProfilesAsync(CancellationToken ct = default) =>
        await ReadAsync<List<IssuanceProfile>>(await http.GetAsync("/api/profiles", ct), ct);

    public async Task<IReadOnlyList<DirectoryUser>> SearchAsync(string query, CancellationToken ct = default) =>
        await ReadAsync<List<DirectoryUser>>(
            await http.GetAsync($"/api/directory/users?q={Uri.EscapeDataString(query)}", ct), ct);

    public async Task<ReservationResponse> ReserveAsync(StartIssuanceRequest request, CancellationToken ct = default) =>
        await ReadAsync<ReservationResponse>(
            await http.PostAsJsonAsync("/api/issuances", request, Json, ct), ct);

    public Task CustomisedAsync(Guid id, CancellationToken ct = default) =>
        PostAsync(id, "customised", ct);

    public Task AttestedAsync(Guid id, AttestationUpload upload, CancellationToken ct = default) =>
        PostAsync(id, "attestation", upload, ct);

    public Task SubmittedAsync(Guid id, SubmittedRequest request, CancellationToken ct = default) =>
        PostAsync(id, "submitted", request, ct);

    public Task PendingAsync(Guid id, CancellationToken ct = default) =>
        PostAsync(id, "pending", ct);

    public Task CompleteAsync(Guid id, byte[] certificate, CancellationToken ct = default) =>
        PostAsync(id, "complete", new CompleteRequest(certificate), ct);

    public Task FailedAsync(Guid id, string error, CancellationToken ct = default) =>
        PostAsync(id, "failed", new FailedRequest(error), ct);

    private async Task PostAsync(Guid id, string step, CancellationToken ct) =>
        await ReadAsync<IssuanceStatus>(
            await http.PostAsync($"/api/issuances/{id}/{step}", content: null, ct), ct);

    private async Task PostAsync<T>(Guid id, string step, T body, CancellationToken ct) =>
        await ReadAsync<IssuanceStatus>(
            await http.PostAsJsonAsync($"/api/issuances/{id}/{step}", body, Json, ct), ct);

    /// <summary>
    /// The body, or the server's refusal turned into an exception carrying its
    /// message key.
    /// </summary>
    private static async Task<T> ReadAsync<T>(HttpResponseMessage response, CancellationToken ct)
    {
        if (response.IsSuccessStatusCode)
        {
            return await response.Content.ReadFromJsonAsync<T>(Json, ct)
                ?? throw new ServerException(response.StatusCode, ErrorCodes.Internal, "an empty body");
        }

        var problem = await ProblemAsync(response, ct);

        throw new ServerException(response.StatusCode, problem.Code, problem.Detail);
    }

    private static async Task<(string Code, string? Detail)> ProblemAsync(
        HttpResponseMessage response, CancellationToken ct)
    {
        try
        {
            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
            var root = document.RootElement;

            return (
                root.TryGetProperty(ErrorCodes.ProblemCodeKey, out var code)
                    ? code.GetString() ?? ErrorCodes.Internal
                    : ErrorCodes.Internal,
                root.TryGetProperty("detail", out var detail) ? detail.GetString() : null);
        }
        catch (JsonException)
        {
            // Something that is not the server answered - a proxy, a portal, a
            // TLS interception page. Saying so beats "internal error".
            return (ErrorCodes.Internal, $"the response was not ProblemDetails ({(int)response.StatusCode})");
        }
    }

    public void Dispose() => http.Dispose();
}

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

    /// <summary>Signs in with the operator's AD credentials and keeps the token in memory.</summary>
    public async Task<CurrentUser> LoginAsync(string username, string password, CancellationToken ct = default)
    {
        var response = await http.PostAsJsonAsync("/api/auth/login", new LoginRequest(username, password), Json, ct);
        var login = await ReadAsync<LoginResponse>(response, ct);

        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", login.Token);
        User = login.User;

        return login.User;
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

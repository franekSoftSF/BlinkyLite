using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using BlinkyLite.Contracts;

namespace BlinkyLite.Unlock;

/// <summary>The server refused, with the key it sent.</summary>
public sealed class RemoteRefusedException(HttpStatusCode status, string messageKey)
    : Exception($"{messageKey} ({(int)status})")
{
    public string MessageKey { get; } = messageKey;
}

/// <summary>
/// The telephone half of 0057: ask for a code, wait for somebody to approve it,
/// collect the PUK, say how it went.
/// </summary>
/// <remarks>
/// <para>
/// No token anywhere in here, because there cannot be one - whoever is at this
/// desk has a blocked PIN, which is the state of having nothing to sign in
/// with. What takes its place is the secret the server hands back with the
/// code: it stays in this object, in memory, and the code alone collects
/// nothing without it.
/// </para>
/// <para>
/// The PUK that comes back is passed straight to the card and never shown,
/// logged or stored. The person who reads out the code never learns it, which
/// is the whole reason this exists instead of the helpdesk reading it out.
/// </para>
/// </remarks>
public sealed class RemoteUnlock : IDisposable
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>Often enough to feel immediate, rarely enough not to look like a flood in the log.</summary>
    public static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(3);

    private readonly HttpClient http;

    public RemoteUnlock(Uri server)
    {
        http = new HttpClient { BaseAddress = server, Timeout = TimeSpan.FromSeconds(20) };
    }

    public async Task<UnlockTicket> AskAsync(long cardSerial, string workstation, CancellationToken ct) =>
        await ReadAsync<UnlockTicket>(
            await http.PostAsJsonAsync("/api/unlock/start", new UnlockRequest(cardSerial, workstation), Json, ct), ct);

    public async Task<UnlockState> StateAsync(UnlockTicket ticket, CancellationToken ct) =>
        await ReadAsync<UnlockState>(
            await http.PostAsJsonAsync($"/api/unlock/{ticket.RequestId}/state", new UnlockSecret(ticket.Secret), Json, ct), ct);

    /// <summary>
    /// What the card said. Sent on failure as well as on success: a request
    /// that stops at "handed out" leaves whoever reads the audit trail
    /// guessing, and this is the one line that says whether it worked.
    /// </summary>
    public async Task ReportAsync(UnlockTicket ticket, bool ok, string? error, CancellationToken ct)
    {
        var response = await http.PostAsJsonAsync($"/api/unlock/{ticket.RequestId}/result",
            new UnlockOutcome(ticket.Secret, ok, error), Json, ct);

        if (!response.IsSuccessStatusCode)
        {
            throw new RemoteRefusedException(response.StatusCode, await CodeAsync(response, ct));
        }
    }

    public void Dispose() => http.Dispose();

    private static async Task<T> ReadAsync<T>(HttpResponseMessage response, CancellationToken ct)
    {
        if (!response.IsSuccessStatusCode)
        {
            throw new RemoteRefusedException(response.StatusCode, await CodeAsync(response, ct));
        }

        return await response.Content.ReadFromJsonAsync<T>(Json, ct)
               ?? throw new RemoteRefusedException(response.StatusCode, ErrorCodes.Internal);
    }

    private static async Task<string> CodeAsync(HttpResponseMessage response, CancellationToken ct)
    {
        try
        {
            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));

            return document.RootElement.TryGetProperty(ErrorCodes.ProblemCodeKey, out var code)
                ? code.GetString() ?? ErrorCodes.Internal
                : ErrorCodes.Internal;
        }
        catch (JsonException)
        {
            // Not our server, or not our server today: nginx says this in HTML.
            return ErrorCodes.Internal;
        }
    }
}

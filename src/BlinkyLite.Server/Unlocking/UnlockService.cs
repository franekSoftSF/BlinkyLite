using System.Net;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using BlinkyLite.Contracts;
using BlinkyLite.Server.Api;
using BlinkyLite.Server.Auth;
using BlinkyLite.Server.Data;
using BlinkyLite.Server.Secrets;

namespace BlinkyLite.Server.Unlocking;

/// <summary>
/// Remote PIN unblock (0057, D-37): a code read out over the telephone, an
/// operator's approval, and the PUK delivered to the machine that asked -
/// never to the person.
/// </summary>
/// <remarks>
/// <para>
/// Unblocking a PIN needs the PUK itself; the card has no challenge-response
/// that could stand in for it. So the only question was who hears the PUK, and
/// the answer here is nobody: it travels over TLS to the application that will
/// use it, once, after somebody with a role has approved that exact request.
/// </para>
/// <para>
/// Three of these calls arrive with nobody signed in, because a blocked PIN is
/// the state of having nothing to sign in with. What replaces a token is a
/// secret the asking application generates and keeps; the server stores only
/// its SHA-256. A code overheard on the telephone therefore buys nothing.
/// </para>
/// </remarks>
public sealed class UnlockService(
    IProcedures procedures,
    SecretEnvelopes envelopes,
    TimeProvider clock,
    ILogger<UnlockService> logger)
{
    /// <summary>Long enough for a telephone call, short enough that a written-down code goes stale.</summary>
    public const int Minutes = 10;

    private const int CodeLetters = 6;

    /// <summary>
    /// Without 0/O and 1/I/L: this is read out loud, and a code somebody has
    /// to spell is a code they read wrong.
    /// </summary>
    private const string Alphabet = "ABCDEFGHJKMNPQRSTUVWXYZ23456789";

    public async Task<IResult> RequestAsync(UnlockRequest? request, IPAddress? sourceIp, CancellationToken ct)
    {
        if (request is null || request.CardSerial <= 0 || (request.Workstation?.Length ?? 0) > 64)
        {
            return Problems.Of(StatusCodes.Status400BadRequest, ErrorCodes.BadRequest);
        }

        var id = Guid.NewGuid();
        var code = Code();
        var secret = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));

        await procedures.RequestUnlockAsync(id, request.CardSerial, code, Hash(secret),
            request.Workstation ?? "", Minutes, Anonymous(sourceIp), ct);

        logger.LogInformation("Unlock request {Request} for card {Serial} from {Workstation}",
            id, request.CardSerial, request.Workstation);

        return Results.Ok(new UnlockTicket(id, code, secret, clock.GetUtcNow().AddMinutes(Minutes)));
    }

    /// <summary>How far the request got, and - once, after an approval - the PUK.</summary>
    public async Task<IResult> StateAsync(Guid id, UnlockSecret? body, IPAddress? sourceIp, CancellationToken ct)
    {
        if (body is null || string.IsNullOrWhiteSpace(body.Secret))
        {
            return Problems.Of(StatusCodes.Status400BadRequest, ErrorCodes.BadRequest);
        }

        var delivery = await procedures.CollectUnlockAsync(id, Hash(body.Secret), Anonymous(sourceIp), ct);

        if (delivery.Envelope is null || delivery.IssuanceId is not { } issuanceId)
        {
            return Results.Ok(new UnlockState(delivery.State));
        }

        var puk = envelopes.Open(delivery.Envelope,
            new EnvelopeBinding(EnvelopeKind.Puk, delivery.CardSerial, issuanceId));
        try
        {
            logger.LogWarning("PUK of card {Serial} delivered for unlock request {Request}", delivery.CardSerial, id);

            return Results.Ok(new UnlockState(delivery.State, Encoding.ASCII.GetString(puk)));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(puk);
        }
    }

    /// <summary>What the card said, so that the trail does not end at "handed out".</summary>
    public async Task<IResult> FinishAsync(Guid id, UnlockOutcome? outcome, IPAddress? sourceIp, CancellationToken ct)
    {
        if (outcome is null || string.IsNullOrWhiteSpace(outcome.Secret))
        {
            return Problems.Of(StatusCodes.Status400BadRequest, ErrorCodes.BadRequest);
        }

        await procedures.FinishUnlockAsync(id, Hash(outcome.Secret), outcome.Ok, outcome.Error, Anonymous(sourceIp), ct);
        logger.LogInformation("Unlock request {Request} finished: {Ok}", id, outcome.Ok);

        return Results.NoContent();
    }

    public async Task<IResult> WaitingAsync(ClaimsPrincipal principal, IPAddress? sourceIp, CancellationToken ct)
    {
        var rows = await procedures.UnlockWaitingAsync(ActorOf(principal, sourceIp), ct);

        return Results.Ok(rows.Select(r => new UnlockWaiting(
            r.Id, r.Code, r.CardSerial, r.Workstation, r.SourceIp,
            Utc(r.CreatedAt), Utc(r.ExpiresAt), r.TargetDisplayName, r.TargetSam)).ToList());
    }

    public async Task<IResult> DecideAsync(
        Guid id, bool approve, UnlockDecision? decision, ClaimsPrincipal principal, IPAddress? sourceIp, CancellationToken ct)
    {
        var actor = ActorOf(principal, sourceIp);
        await procedures.DecideUnlockAsync(id, approve, decision?.Reason ?? "", actor, ct);
        logger.LogWarning("Unlock request {Request} {Decision} by {Upn}", id, approve ? "approved" : "refused", actor.Upn);

        return Results.NoContent();
    }

    private static string Code()
    {
        var letters = RandomNumberGenerator.GetString(Alphabet, CodeLetters);

        // Grouped, because that is how somebody reads it out and how the
        // operator types it back.
        return $"{letters[..3]}-{letters[3..]}";
    }

    public static string Normalise(string code) =>
        new(code.Where(char.IsLetterOrDigit).Select(char.ToUpperInvariant).ToArray());

    private static byte[] Hash(string secret) => SHA256.HashData(Encoding.UTF8.GetBytes(secret));

    /// <summary>
    /// The actor behind the three calls nobody signs in for. A name, not a
    /// person: the audit says the request did this, and who approved it is on
    /// the approval event.
    /// </summary>
    private static Actor Anonymous(IPAddress? sourceIp) => new("system:unlock", "S-1-0-0", [], sourceIp);

    private static Actor ActorOf(ClaimsPrincipal principal, IPAddress? sourceIp)
    {
        var user = TokenService.CurrentUser(principal);
        return new Actor(user.Upn, user.Sid, user.Roles, sourceIp);
    }

    private static DateTimeOffset Utc(DateTime value) => new(DateTime.SpecifyKind(value, DateTimeKind.Utc));
}

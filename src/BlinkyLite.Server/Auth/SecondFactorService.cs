using System.Net;
using System.Security.Claims;
using System.Security.Cryptography;
using BlinkyLite.Contracts;
using BlinkyLite.Server.Api;
using BlinkyLite.Server.Data;
using BlinkyLite.Server.Secrets;

namespace BlinkyLite.Server.Auth;

/// <summary>
/// Sign-in, second step: the TOTP code or a backup code for the ticket that a
/// correct password earned, and the first-time setup (0027, D-31).
/// </summary>
/// <remarks>
/// <para>
/// The secret is opened here, compared here and zeroed here. It is never
/// logged, never audited and never returned - except once, by
/// <see cref="SetupAsync"/>, to the person it belongs to.
/// </para>
/// <para>
/// Which step a code belongs to is worked out here; whether that step may
/// still be used is decided by bl_totp_accept under a row lock, so the same
/// code sent twice at the same moment signs in once.
/// </para>
/// </remarks>
public sealed class SecondFactorService(
    IProcedures procedures,
    SecretEnvelopes envelopes,
    TokenService tokens,
    FailedLogins failures,
    TimeProvider clock,
    ILogger<SecondFactorService> logger)
{
    public const string Issuer = "BlinkyLite";
    private const int MaxCodeLength = 32;

    public async Task<IResult> SetupAsync(ClaimsPrincipal principal, IPAddress? sourceIp, CancellationToken ct)
    {
        var user = TokenService.CurrentUser(principal);
        var actor = new Actor(user.Upn, user.Sid, user.Roles, sourceIp);

        if (await procedures.GetTotpAsync(actor, ct) is { Confirmed: true })
        {
            return Problems.Of(StatusCodes.Status409Conflict, ErrorCodes.TotpAlreadyConfigured);
        }

        var secret = RandomNumberGenerator.GetBytes(Totp.SecretBytes);
        try
        {
            await procedures.BeginTotpAsync(envelopes.SealTotp(secret, user.Sid), envelopes.CurrentVersion, actor, ct);
            logger.LogInformation("Second factor setup started for {Upn}", user.Upn);

            return Results.Ok(new TotpSetupResponse(Base32.Encode(secret), Totp.OtpAuthUri(Issuer, user.Upn, secret)));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(secret);
        }
    }

    public async Task<IResult> VerifyAsync(
        ClaimsPrincipal principal, SecondFactorRequest? request, IPAddress? sourceIp, CancellationToken ct)
    {
        var user = TokenService.CurrentUser(principal);
        var actor = new Actor(user.Upn, user.Sid, user.Roles, sourceIp);

        if (request is null || string.IsNullOrWhiteSpace(request.Code) || request.Code.Length > MaxCodeLength)
        {
            return Problems.Of(StatusCodes.Status400BadRequest, ErrorCodes.BadRequest);
        }

        // Six digits are a million guesses; the ticket lasts five minutes and
        // could be replayed for all of them without this.
        if (failures.IsLockedOut(user.Upn))
        {
            await Deny(actor, "locked-out", ct);
            return Problems.Of(StatusCodes.Status429TooManyRequests, ErrorCodes.RateLimited);
        }

        var state = await procedures.GetTotpAsync(actor, ct);
        if (state is null)
        {
            return Problems.Of(StatusCodes.Status409Conflict, ErrorCodes.TotpSetupRequired);
        }

        var code = request.Code.Trim();
        long? step;
        var secret = envelopes.OpenTotp(state.SecretEnvelope, user.Sid);
        try
        {
            step = Totp.Match(secret, code, clock.GetUtcNow());
        }
        finally
        {
            CryptographicOperations.ZeroMemory(secret);
        }

        if (step is { } matched)
        {
            return state.Confirmed
                ? await Accept(user, actor, matched, ct)
                : await Confirm(user, actor, state.KekVersion, matched, ct);
        }

        // A backup code stands in for the app, not for setting it up: an
        // unconfirmed secret has none yet.
        if (state.Confirmed && BackupCodes.Normalise(code) is { } backup)
        {
            return await UseBackup(user, actor, envelopes.BackupCodeHash(state.KekVersion, user.Sid, backup), ct);
        }

        return await Refuse(actor, "totp-invalid", ct);
    }

    public async Task<IResult> ResetAsync(
        string operatorSid, TotpResetRequest? request, ClaimsPrincipal principal, IPAddress? sourceIp, CancellationToken ct)
    {
        var admin = TokenService.CurrentUser(principal);

        await procedures.ResetTotpAsync(operatorSid, request?.Reason ?? "",
            new Actor(admin.Upn, admin.Sid, admin.Roles, sourceIp), ct);
        logger.LogWarning("Second factor of {OperatorSid} reset by {Upn}", operatorSid, admin.Upn);

        return Results.NoContent();
    }

    private async Task<IResult> Accept(CurrentUser user, Actor actor, long step, CancellationToken ct)
    {
        try
        {
            await procedures.AcceptTotpAsync(step, actor, ct);
        }
        catch (DatabaseRuleException e) when (e.MessageKey == ErrorCodes.TotpInvalid)
        {
            // The right code, already used. Counted like a wrong one: whoever
            // replays a code they saw is guessing too.
            return await Refuse(actor, "totp-replayed", ct);
        }

        return SignedIn(user, tokens.Issue(user));
    }

    private async Task<IResult> Confirm(CurrentUser user, Actor actor, short kekVersion, long step, CancellationToken ct)
    {
        var codes = BackupCodes.Generate();
        var hashes = codes.Select(c => envelopes.BackupCodeHash(kekVersion, user.Sid, BackupCodes.Normalise(c)!)).ToList();

        await procedures.ConfirmTotpAsync(step, hashes, actor, ct);
        logger.LogInformation("Second factor confirmed for {Upn}", user.Upn);

        return SignedIn(user, tokens.Issue(user) with { BackupCodes = codes });
    }

    private async Task<IResult> UseBackup(CurrentUser user, Actor actor, byte[] hash, CancellationToken ct)
    {
        int left;
        try
        {
            left = await procedures.UseBackupCodeAsync(hash, actor, ct);
        }
        catch (DatabaseRuleException e) when (e.MessageKey == ErrorCodes.TotpInvalid)
        {
            return await Refuse(actor, "backup-code-invalid", ct);
        }

        logger.LogWarning("{Upn} signed in with a backup code; {Left} left", user.Upn, left);
        return SignedIn(user, tokens.Issue(user) with { BackupCodesLeft = left });
    }

    private IResult SignedIn(CurrentUser user, LoginResponse response)
    {
        failures.Reset(user.Upn);
        logger.LogInformation("Signed in {Upn} with roles {Roles}", user.Upn, user.Roles);
        return Results.Ok(response);
    }

    private async Task<IResult> Refuse(Actor actor, string reason, CancellationToken ct)
    {
        failures.Record(actor.Upn);
        await Deny(actor, reason, ct);
        return Problems.Of(StatusCodes.Status401Unauthorized, ErrorCodes.TotpInvalid);
    }

    private async Task Deny(Actor actor, string reason, CancellationToken ct)
    {
        logger.LogWarning("Second factor refused for {Upn}: {Reason}", actor.Upn, reason);
        await procedures.AuditAsync(AuditAction.AuthDenied, new { reason }, actor, ct);
    }
}

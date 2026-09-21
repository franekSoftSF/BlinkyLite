using System.Collections.Concurrent;
using System.Net;
using System.Security.Claims;
using BlinkyLite.Contracts;
using BlinkyLite.Server.Api;
using BlinkyLite.Server.Data;

namespace BlinkyLite.Server.Auth;

/// <summary>
/// Sign-in, first step: bind to AD as the operator and map group SIDs to
/// roles. A correct password earns a ticket for the second factor, never a
/// token (0027) - the token comes from <see cref="SecondFactorService"/>, and
/// the sign-in is audited there, once it has actually happened. Every refusal
/// here is audited as auth.denied with its reason.
/// </summary>
public sealed class LoginService(
    IDirectory directory,
    RoleMap roles,
    TokenService tokens,
    IProcedures procedures,
    FailedLogins failures,
    KerberosOptions kerberos,
    ILogger<LoginService> logger)
{
    private const int MaxLength = 256;

    public async Task<IResult> LoginAsync(LoginRequest? request, IPAddress? sourceIp, CancellationToken ct)
    {
        if (request is null
            || string.IsNullOrWhiteSpace(request.Username) || request.Username.Length > MaxLength
            || string.IsNullOrEmpty(request.Password) || request.Password.Length > MaxLength)
        {
            return Problems.Of(StatusCodes.Status400BadRequest, ErrorCodes.BadRequest);
        }

        var username = request.Username.Trim();

        // Checked before asking AD: the server must not become a way to lock an
        // account out by guessing, nor an oracle for passwords.
        if (failures.IsLockedOut(username))
        {
            await Deny(username, sid: null, "locked-out", sourceIp, ct);
            return Problems.Of(StatusCodes.Status429TooManyRequests, ErrorCodes.RateLimited);
        }

        var account = await directory.AuthenticateAsync(username, request.Password, ct);
        if (account is null)
        {
            failures.Record(username);
            await Deny(username, sid: null, "invalid-credentials", sourceIp, ct);
            return Problems.Of(StatusCodes.Status401Unauthorized, ErrorCodes.InvalidCredentials);
        }

        return await ChallengeFor(account, "password", sourceIp, ct);
    }

    /// <summary>
    /// The first step with a Kerberos ticket instead of a password (0025). The
    /// Negotiate handler has already checked the ticket against the keytab;
    /// what is left is the same as after a password: who is it in AD, which
    /// roles, and then the second factor - Kerberos does not replace it (D-31).
    /// </summary>
    public async Task<IResult> LoginWithKerberosAsync(ClaimsPrincipal principal, IPAddress? sourceIp, CancellationToken ct)
    {
        var name = principal.Identity?.Name ?? "";
        if (KerberosName.Parse(name) is not { } parsed)
        {
            await Deny(name.Length == 0 ? "(kerberos)" : name, sid: null, "kerberos-unreadable-name", sourceIp, ct);
            return Problems.Of(StatusCodes.Status403Forbidden, ErrorCodes.Forbidden);
        }

        // A ticket from a trusted realm is still a ticket for somebody this
        // server's AD does not describe; sAMAccountNames are only unique
        // within one domain.
        if (!kerberos.AcceptsRealm(parsed.Realm))
        {
            await Deny(name, sid: null, "kerberos-foreign-realm", sourceIp, ct);
            return Problems.Of(StatusCodes.Status403Forbidden, ErrorCodes.Forbidden);
        }

        if (failures.IsLockedOut(parsed.Account))
        {
            await Deny(name, sid: null, "locked-out", sourceIp, ct);
            return Problems.Of(StatusCodes.Status429TooManyRequests, ErrorCodes.RateLimited);
        }

        var account = await directory.FindForKerberosAsync(parsed.Account, ct);
        if (account is null)
        {
            await Deny(name, sid: null, "kerberos-no-account", sourceIp, ct);
            return Problems.Of(StatusCodes.Status403Forbidden, ErrorCodes.KerberosNoAccount);
        }

        if (!account.User.Enabled)
        {
            await Deny(name, account.User.Sid, "disabled", sourceIp, ct);
            return Problems.Of(StatusCodes.Status403Forbidden, ErrorCodes.Forbidden);
        }

        return await ChallengeFor(account, "kerberos", sourceIp, ct);
    }

    /// <summary>After AD said who it is, by whichever route: roles, then a ticket for the second factor.</summary>
    private async Task<IResult> ChallengeFor(DirectoryAccount account, string method, IPAddress? sourceIp, CancellationToken ct)
    {
        var granted = roles.RolesFor(account.GroupSids);
        var upn = string.IsNullOrEmpty(account.User.Upn) ? account.User.SamAccount : account.User.Upn;
        if (granted.Count == 0)
        {
            // The identity was proven, so this is not a failed guess.
            await Deny(upn, account.User.Sid, "no-role", sourceIp, ct);
            return Problems.Of(StatusCodes.Status403Forbidden, ErrorCodes.NoRole);
        }

        // Not reset here: a right password followed by five wrong codes is
        // still five failures. SecondFactorService resets on a real sign-in.
        var user = new CurrentUser(upn, account.User.Sid, account.User.DisplayName, granted);

        var totp = await procedures.GetTotpAsync(new Actor(user.Upn, user.Sid, granted, sourceIp), ct);
        var next = totp is { Confirmed: true } ? LoginNext.Totp : LoginNext.TotpSetup;
        logger.LogInformation("First step ({Method}) accepted for {Upn}; next step {Next}", method, user.Upn, next);

        return Results.Ok(tokens.IssueTicket(user, next));
    }

    private async Task Deny(string upn, string? sid, string reason, IPAddress? sourceIp, CancellationToken ct)
    {
        logger.LogWarning("Sign-in refused for {Username}: {Reason}", upn, reason);
        await procedures.AuditAsync(AuditAction.AuthDenied, new { reason }, new Actor(upn, sid, [], sourceIp), ct);
    }
}

/// <summary>
/// Failed sign-ins per account, in memory. Five within fifteen minutes and the
/// account is refused here until the window ends - well before AD's own
/// lockout, so that BlinkyLite cannot be used to lock people out of Windows.
/// </summary>
public sealed class FailedLogins(TimeProvider clock)
{
    public const int Limit = 5;
    public static readonly TimeSpan Window = TimeSpan.FromMinutes(15);

    private readonly ConcurrentDictionary<string, (int Count, DateTimeOffset Since)> failures = new(StringComparer.OrdinalIgnoreCase);

    public bool IsLockedOut(string username) =>
        failures.TryGetValue(Key(username), out var entry)
        && entry.Count >= Limit
        && clock.GetUtcNow() - entry.Since < Window;

    public void Record(string username) =>
        failures.AddOrUpdate(Key(username),
            _ => (1, clock.GetUtcNow()),
            (_, entry) => clock.GetUtcNow() - entry.Since >= Window ? (1, clock.GetUtcNow()) : (entry.Count + 1, entry.Since));

    public void Reset(string username) => failures.TryRemove(Key(username), out _);

    // CORP\jkowalski, jkowalski and jkowalski@corp.example are one account.
    private static string Key(string username)
    {
        var name = username.Trim();
        var slash = name.IndexOf('\\', StringComparison.Ordinal);
        if (slash >= 0)
        {
            name = name[(slash + 1)..];
        }

        var at = name.IndexOf('@', StringComparison.Ordinal);
        return at >= 0 ? name[..at] : name;
    }
}

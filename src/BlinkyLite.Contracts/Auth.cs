namespace BlinkyLite.Contracts;

/// <summary>Body of <c>POST /api/auth/login</c>. The username may be <c>jkowalski</c>, <c>CORP\jkowalski</c> or a UPN.</summary>
public sealed record LoginRequest(string Username, string Password);

/// <summary>
/// The answer to a correct password: never a token, only a ticket for the
/// second step (0027, D-31).
/// </summary>
/// <param name="Next"><see cref="LoginNext.Totp"/> or <see cref="LoginNext.TotpSetup"/>.</param>
/// <param name="Ticket">
/// Sent as the bearer on <c>/api/auth/totp*</c> and accepted nowhere else - the
/// server signs it for another audience than the access token.
/// </param>
public sealed record LoginChallenge(string Next, string Ticket, DateTimeOffset ExpiresAt);

public static class LoginNext
{
    /// <summary>The operator has a second factor: ask for the code.</summary>
    public const string Totp = "totp";

    /// <summary>
    /// No second factor yet. It is mandatory, so the only thing this ticket
    /// allows is setting one up - in the web console, where a QR code can be
    /// shown.
    /// </summary>
    public const string TotpSetup = "totp-setup";
}

/// <summary>Body of <c>POST /api/auth/totp</c>: six digits from the app, or a backup code <c>ABCDE-FGHJK</c>.</summary>
public sealed record SecondFactorRequest(string Code);

/// <summary>A new TOTP secret, returned once by <c>POST /api/auth/totp/setup</c>.</summary>
/// <param name="Secret">Base32, for typing in when the QR code cannot be scanned.</param>
/// <param name="OtpAuthUri">
/// For the QR code. The console draws it itself - handing the secret to an
/// online QR generator would give it away.
/// </param>
public sealed record TotpSetupResponse(string Secret, string OtpAuthUri);

/// <param name="BackupCodes">
/// Only on the sign-in that confirmed a new second factor, and never again:
/// the server keeps HMACs of them, not the codes.
/// </param>
/// <param name="BackupCodesLeft">After signing in with a backup code: how many unused ones remain.</param>
public sealed record LoginResponse(
    string Token,
    DateTimeOffset ExpiresAt,
    CurrentUser User,
    IReadOnlyList<string>? BackupCodes = null,
    int? BackupCodesLeft = null);

/// <summary>Who the token belongs to; <c>GET /api/auth/me</c> returns the same.</summary>
public sealed record CurrentUser(string Upn, string Sid, string DisplayName, IReadOnlyList<Role> Roles);

/// <summary>Body of <c>POST /api/operators/{sid}/totp/reset</c>.</summary>
public sealed record TotpResetRequest(string Reason);

/// <summary>A person found in AD who can receive a key.</summary>
/// <param name="SamAccount"><c>DOMAIN\sAMAccountName</c> - exactly what goes into the CMC RequesterName.</param>
public sealed record DirectoryUser(string SamAccount, string Upn, string Sid, string DisplayName, bool Enabled);

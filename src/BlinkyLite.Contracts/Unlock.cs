namespace BlinkyLite.Contracts;

/// <summary>
/// Remote PIN unblock (0057, D-37): the person reads out a code, an operator
/// approves it in the console, and the application collects the PUK itself.
/// </summary>
/// <remarks>
/// Nobody is signed in on the asking side - a blocked PIN is exactly the state
/// of having nothing to sign in with - so what stands in for a token is a
/// secret the application makes up, keeps, and shows on every later call.
/// </remarks>
public sealed record UnlockRequest(long CardSerial, string Workstation);

/// <param name="Code">What the person reads out to the operator. Short, and useless on its own.</param>
/// <param name="Secret">
/// Stays in the asking application. The server keeps only its hash; without it
/// the PUK is not handed over, so knowing the code is not enough.
/// </param>
public sealed record UnlockTicket(Guid RequestId, string Code, string Secret, DateTimeOffset ExpiresAt);

/// <summary>Body of the two calls that carry the secret back.</summary>
public sealed record UnlockSecret(string Secret);

/// <param name="Puk">Filled once, on the call that finds the request approved. Never again.</param>
public sealed record UnlockState(string State, string? Puk = null);

public sealed record UnlockOutcome(string Secret, bool Ok, string? Error);

/// <summary>What the console shows before somebody decides.</summary>
public sealed record UnlockWaiting(
    Guid Id,
    string Code,
    long CardSerial,
    string Workstation,
    string? SourceIp,
    DateTimeOffset CreatedAt,
    DateTimeOffset ExpiresAt,
    string TargetDisplayName,
    string TargetSam);

/// <summary>Body of approve and refuse; the reason goes into the audit trail.</summary>
public sealed record UnlockDecision(string Reason);

/// <summary>The states of one request, as the database spells them.</summary>
public static class UnlockStates
{
    public const string Pending = "Pending";
    public const string Approved = "Approved";
    public const string Delivered = "Delivered";
    public const string Completed = "Completed";
    public const string Failed = "Failed";
    public const string Refused = "Refused";
    public const string Expired = "Expired";
}

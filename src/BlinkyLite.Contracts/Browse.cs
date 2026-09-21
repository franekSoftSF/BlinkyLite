namespace BlinkyLite.Contracts;

/// <summary>One page of a list.</summary>
/// <remarks>
/// Paged from the first day: a list endpoint without a limit is fine with fifty
/// cards and takes the server down with five thousand, and nothing marks the
/// moment in between.
/// </remarks>
public sealed record Page<T>(IReadOnlyList<T> Items, long Total, int PageNumber, int PageSize);

/// <summary>
/// A row of <c>GET /api/issuances</c>, for every role - Helpdesk included.
/// </summary>
/// <remarks>
/// Who, which card, when, and how it ended; nothing else. Not a trimmed
/// <see cref="IssuanceDetails"/>: a separate type, so that a field added to the
/// details cannot reach Helpdesk by accident (a test compares the JSON).
/// </remarks>
public sealed record IssuanceListItem(
    Guid Id,
    long CardSerial,
    string TargetDisplayName,
    string TargetSam,
    IssuanceState State,
    DateTimeOffset CreatedAt);

/// <summary>
/// <c>GET /api/issuances/{id}</c>: everything the record says about one
/// issuance except the blobs, for Admin and SecurityOfficer.
/// </summary>
/// <param name="IsCurrent">Whether this is the card's current issuance - the one whose PUK is disclosed.</param>
/// <param name="PukDisclosedCount">How often this issuance's PUK has been disclosed; each time is in the audit.</param>
public sealed record IssuanceDetails(
    Guid Id,
    long CardSerial,
    string Firmware,
    IssuanceState State,
    bool IsCurrent,
    string TargetDisplayName,
    string TargetSam,
    string TargetUpn,
    string TargetSid,
    string ProfileName,
    string TemplateName,
    string CaConfig,
    int? CaRequestId,
    string OperatorUpn,
    string WindowsIdentity,
    string Workstation,
    string? EnrolmentAgentThumbprint,
    string? KeyAlgorithm,
    short? PinPolicy,
    short? TouchPolicy,
    string? CertificateSerial,
    string? CertificateThumbprint,
    DateTimeOffset? CertificateNotBefore,
    DateTimeOffset? CertificateNotAfter,
    string? Error,
    int PukDisclosedCount,
    DateTimeOffset CreatedAt,
    DateTimeOffset? CompletedAt);

/// <summary>
/// <c>GET /api/cards/{serial}</c>: what "Verify card" compares with the token
/// in the reader - read only, Admin and SecurityOfficer.
/// </summary>
/// <param name="Issuance">The card's current issuance, or null when it has never been issued.</param>
/// <param name="Certificate">The certificate the CA issued, as recorded.</param>
/// <param name="Attestation">The attestation of slot 9A recorded at issuance.</param>
public sealed record CardRecord(
    long Serial,
    IssuanceDetails? Issuance,
    byte[]? Certificate,
    byte[]? Attestation);

/// <summary>Body of the two disclosure endpoints. The reason goes into the audit before the value leaves.</summary>
public sealed record RevealRequest(string Reason);

public sealed record RevealedPuk(long CardSerial, string Puk);

/// <param name="Key">Hex, as <c>ykman</c> takes it.</param>
/// <param name="Algorithm">From <c>GET METADATA 9B</c> at issuance: <c>TripleDes</c>, <c>Aes128</c>, <c>Aes192</c>, <c>Aes256</c>.</param>
public sealed record RevealedManagementKey(long CardSerial, string Key, string Algorithm);

/// <summary>A row of <c>GET /api/audit</c>, Admin only.</summary>
/// <param name="Data">The event's jsonb as it is stored - reasons, roles, ids; never a secret.</param>
public sealed record AuditEntry(
    long Id,
    DateTimeOffset At,
    string ActorUpn,
    IReadOnlyList<string> ActorRoles,
    string Action,
    long? CardSerial,
    Guid? IssuanceId,
    string Data,
    string? SourceIp);

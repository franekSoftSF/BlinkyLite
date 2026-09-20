namespace BlinkyLite.Contracts;

/// <summary>
/// Keys the server puts in a ProblemDetails' <c>code</c>. The server never
/// translates; the client turns the key into text in the operator's language
/// (docs/08-localization.md). Each one needs an entry in Messages.resx.
/// </summary>
public static class ErrorCodes
{
    public const string ProblemCodeKey = "code";
    public const string ProblemArgsKey = "args";

    public const string Internal = "error.internal";
    public const string BadRequest = "error.bad-request";
    public const string NotFound = "error.not-found";
    public const string Forbidden = "error.forbidden";
    public const string AuthRequired = "error.auth.required";
    public const string InvalidCredentials = "error.auth.invalid-credentials";
    public const string NoRole = "error.auth.no-role";
    public const string RateLimited = "error.rate-limited";
    public const string DirectoryUnavailable = "error.directory.unavailable";
    public const string QueryTooShort = "error.query.too-short";

    // What the issuance engine refuses before anything is written to a card.
    public const string CardNotAYubiKey = "error.card.not-a-yubikey";
    public const string CardTooOld = "error.card.too-old";
    public const string CardNoPuk = "error.card.no-puk";
    public const string CardNotFactory = "error.card.not-factory";
    public const string CardSlotOccupied = "error.card.slot-occupied";
    public const string CardNoAttestation = "error.card.no-attestation";
    public const string CardAttestationRefused = "error.card.attestation-refused";
    public const string PinCancelled = "error.pin.cancelled";

    // Issuance API (0020).
    public const string ProfileUnknown = "error.profile.unknown";
    public const string TargetNotFound = "error.target.not-found";
    public const string TargetDisabled = "error.target.disabled";
    public const string CertificateKeyMismatch = "error.certificate.key-mismatch";
    public const string CertificateTargetMismatch = "error.certificate.target-mismatch";

    // Enrol on behalf of (0021).
    public const string AgentMissing = "error.agent.missing";
    public const string CaCmcFailed = "error.ca.cmc-failed";
    public const string CaRefused = "error.ca.refused";
    public const string CaPending = "error.ca.pending";
    public const string CertificateNotWritten = "error.card.certificate-not-written";

    // From the bl_* functions (docs/07-database.md#błędy).
    public const string IssuanceInvalidState = "error.issuance.invalid-state";
    public const string CardReservedElsewhere = "error.card.reserved-elsewhere";
    public const string ReasonRequired = "error.reason.required";
}

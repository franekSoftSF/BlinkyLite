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

    // From the bl_* functions (docs/07-database.md#błędy).
    public const string IssuanceInvalidState = "error.issuance.invalid-state";
    public const string CardReservedElsewhere = "error.card.reserved-elsewhere";
    public const string ReasonRequired = "error.reason.required";
}

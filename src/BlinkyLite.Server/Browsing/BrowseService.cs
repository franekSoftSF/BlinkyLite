using System.Security.Cryptography;
using System.Text;
using BlinkyLite.Contracts;
using BlinkyLite.Piv;
using BlinkyLite.Server.Data;
using BlinkyLite.Server.Secrets;

namespace BlinkyLite.Server.Browsing;

/// <summary>
/// What the browser shows (0030): the list for everybody, the details for
/// those who issue, the secrets one card at a time.
/// </summary>
/// <remarks>
/// Which role sees what is decided by the endpoint's policy and, for the
/// secrets, a second time by bl_secret_disclose. This class only turns rows
/// into the DTO that the policy allows - it never trims a bigger one.
/// </remarks>
public sealed class BrowseService(
    IIssuanceReader reader, IProcedures procedures, SecretEnvelopes envelopes, ILogger<BrowseService> logger)
{
    public const int DefaultPageSize = 50;
    public const int MaxPageSize = 100;

    public Page<IssuanceListItem> List(string? query, int page, int pageSize)
    {
        var rows = reader.List(query, page, pageSize);

        return new Page<IssuanceListItem>(
            rows.Items.Select(i => new IssuanceListItem(
                i.Id, i.CardSerial, i.TargetDisplayName, i.TargetSam, i.State, Utc(i.CreatedAt))).ToList(),
            rows.Total, rows.PageNumber, rows.PageSize);
    }

    public IssuanceDetails? Details(Guid id) =>
        reader.Find(id) is { } issuance ? Details(issuance, reader.FindCard(issuance.CardSerial)) : null;

    public CardRecord? Card(long serial)
    {
        if (reader.FindCard(serial) is not { } card)
        {
            return null;
        }

        var current = card.CurrentIssuanceId is { } id ? reader.Find(id) : null;

        return new CardRecord(
            serial,
            current is null ? null : Details(current, card),
            current?.CertificateDer,
            current?.AttestationDer);
    }

    public async Task<RevealedPuk> RevealPukAsync(long serial, string reason, Actor actor, CancellationToken ct)
    {
        // The audit event is written by the same statement that hands out the
        // envelope; by the time this line returns, the disclosure is on record.
        var sealedPuk = await procedures.DiscloseSecretAsync(serial, SecretKind.Puk, reason, actor, ct);
        logger.LogWarning("PUK of card {Serial} disclosed to {Upn}", serial, actor.Upn);

        var puk = envelopes.Open(sealedPuk.Envelope, new EnvelopeBinding(EnvelopeKind.Puk, serial, sealedPuk.IssuanceId));
        try
        {
            return new RevealedPuk(serial, Encoding.ASCII.GetString(puk));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(puk);
        }
    }

    public async Task<RevealedManagementKey> RevealManagementKeyAsync(
        long serial, string reason, Actor actor, CancellationToken ct)
    {
        var sealedKey = await procedures.DiscloseSecretAsync(serial, SecretKind.ManagementKey, reason, actor, ct);
        logger.LogWarning("Management key of card {Serial} disclosed to {Upn}", serial, actor.Upn);
        var algorithm = (PivAlgorithm)(reader.SecretOf(sealedKey.IssuanceId)?.MgmtKeyAlgorithm ?? 0);

        var key = envelopes.Open(sealedKey.Envelope,
            new EnvelopeBinding(EnvelopeKind.ManagementKey, serial, sealedKey.IssuanceId));
        try
        {
            return new RevealedManagementKey(serial, Convert.ToHexString(key), algorithm.ToString());
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
        }
    }

    public Page<AuditEntry> Audit(long? cardSerial, int page, int pageSize)
    {
        var rows = reader.Audit(cardSerial, page, pageSize);

        return new Page<AuditEntry>(
            rows.Items.Select(a => new AuditEntry(
                a.Id,
                Utc(a.At),
                a.ActorUpn,
                a.ActorRoles.Split(',', StringSplitOptions.RemoveEmptyEntries),
                a.Action,
                a.CardSerial,
                a.IssuanceId,
                a.Data,
                a.SourceIp)).ToList(),
            rows.Total, rows.PageNumber, rows.PageSize);
    }

    /// <summary>Page and size from the query string, clamped rather than refused.</summary>
    public static (int Page, int Size) Paging(int? page, int? pageSize) =>
        (Math.Max(1, page ?? 1), Math.Clamp(pageSize ?? DefaultPageSize, 1, MaxPageSize));

    private IssuanceDetails Details(Issuance i, Card? card) => new(
        i.Id,
        i.CardSerial,
        card?.Firmware ?? "",
        i.State,
        card?.CurrentIssuanceId == i.Id,
        i.TargetDisplayName,
        i.TargetSam,
        i.TargetUpn,
        i.TargetSid,
        i.ProfileName,
        i.TemplateName,
        i.CaConfig,
        i.CaRequestId,
        i.OperatorUpn,
        i.WindowsIdentity,
        i.Workstation,
        i.EaThumbprint,
        i.KeyAlgorithm,
        i.PinPolicy,
        i.TouchPolicy,
        i.CertSerial,
        i.CertThumbprint,
        i.CertNotBefore is { } from ? Utc(from) : null,
        i.CertNotAfter is { } to ? Utc(to) : null,
        i.Error,
        reader.SecretOf(i.Id)?.PukDisclosedCount ?? 0,
        Utc(i.CreatedAt),
        i.CompletedAt is { } done ? Utc(done) : null);

    // timestamptz comes back as UTC; saying so keeps the offset from being
    // guessed from the server's time zone on the way out.
    private static DateTimeOffset Utc(DateTime value) => new(DateTime.SpecifyKind(value, DateTimeKind.Utc));
}

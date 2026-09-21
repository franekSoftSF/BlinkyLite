using BlinkyLite.Contracts;
using NHibernate;
using NHibernate.Linq;

namespace BlinkyLite.Server.Data;

/// <summary>
/// The reads the API needs: an issuance by its id for the issuance steps, and
/// the few questions the browser asks (0030).
/// </summary>
/// <remarks>
/// An interface, not <see cref="ISessionFactory"/> passed around, because the
/// server asks a handful of questions and a session factory is an invitation
/// to ask others - including ones that write. The implementation opens a
/// read-only session like every other read in the server.
/// </remarks>
public interface IIssuanceReader
{
    Issuance? Find(Guid id);

    /// <summary>Newest first. <paramref name="query"/> matches name, account, UPN or the card serial.</summary>
    Page<Issuance> List(string? query, int page, int pageSize);

    Card? FindCard(long serial);

    /// <summary>The envelope bookkeeping of one issuance - never the envelope.</summary>
    CardSecret? SecretOf(Guid issuanceId);

    /// <summary>Newest first; optionally one card's events only.</summary>
    Page<AuditEvent> Audit(long? cardSerial, int page, int pageSize);
}

public sealed class IssuanceReader(ISessionFactory sessions) : IIssuanceReader
{
    public Issuance? Find(Guid id)
    {
        using var session = sessions.OpenReadOnlySession();

        return session.Get<Issuance>(id);
    }

    public Page<Issuance> List(string? query, int page, int pageSize)
    {
        using var session = sessions.OpenReadOnlySession();
        var rows = session.Query<Issuance>();

        if (!string.IsNullOrWhiteSpace(query))
        {
            var text = query.Trim().ToLowerInvariant();

            // A number is also tried as a serial, because that is what is
            // printed on the token the caller at the desk is holding.
            rows = long.TryParse(text, out var serial)
                ? rows.Where(i => i.CardSerial == serial
                                  || i.TargetDisplayName.ToLower().Contains(text)
                                  || i.TargetSam.ToLower().Contains(text))
                : rows.Where(i => i.TargetDisplayName.ToLower().Contains(text)
                                  || i.TargetSam.ToLower().Contains(text)
                                  || i.TargetUpn.ToLower().Contains(text));
        }

        var total = rows.LongCount();
        var items = rows
            .OrderByDescending(i => i.CreatedAt)
            .ThenByDescending(i => i.Id)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToList();

        return new Page<Issuance>(items, total, page, pageSize);
    }

    public Card? FindCard(long serial)
    {
        using var session = sessions.OpenReadOnlySession();

        return session.Get<Card>(serial);
    }

    public CardSecret? SecretOf(Guid issuanceId)
    {
        using var session = sessions.OpenReadOnlySession();

        return session.Query<CardSecret>().SingleOrDefault(s => s.IssuanceId == issuanceId);
    }

    public Page<AuditEvent> Audit(long? cardSerial, int page, int pageSize)
    {
        using var session = sessions.OpenReadOnlySession();
        var rows = session.Query<AuditEvent>();

        if (cardSerial is { } serial)
        {
            rows = rows.Where(a => a.CardSerial == serial);
        }

        var total = rows.LongCount();
        var items = rows
            .OrderByDescending(a => a.Id)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToList();

        return new Page<AuditEvent>(items, total, page, pageSize);
    }
}

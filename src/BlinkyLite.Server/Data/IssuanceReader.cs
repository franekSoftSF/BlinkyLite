using NHibernate;

namespace BlinkyLite.Server.Data;

/// <summary>
/// The one read the issuance API needs: an issuance by its id.
/// </summary>
/// <remarks>
/// An interface, not <see cref="ISessionFactory"/> passed around, because the
/// service asks one question and a session factory is an invitation to ask
/// others - including ones that write. The implementation opens a read-only
/// session like every other read in the server.
/// </remarks>
public interface IIssuanceReader
{
    Issuance? Find(Guid id);
}

public sealed class IssuanceReader(ISessionFactory sessions) : IIssuanceReader
{
    public Issuance? Find(Guid id)
    {
        using var session = sessions.OpenReadOnlySession();

        return session.Get<Issuance>(id);
    }
}

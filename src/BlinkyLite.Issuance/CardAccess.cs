using BlinkyLite.Piv;
using BlinkyLite.Piv.Pcsc;

namespace BlinkyLite.Issuance;

/// <summary>A token found in a reader, held open until it is disposed.</summary>
public sealed class CardHandle : IDisposable
{
    private readonly PcscContext context;
    private readonly IDisposable transport;
    private readonly PivConnection connection;

    internal CardHandle(PcscContext context, IDisposable transport, PivConnection connection, string reader)
    {
        this.context = context;
        this.transport = transport;
        this.connection = connection;

        Reader = reader;
        Session = new PivSession(connection);
    }

    public string Reader { get; }

    public PivSession Session { get; }

    public void Dispose()
    {
        connection.Dispose();
        transport.Dispose();
        context.Dispose();
    }
}

/// <summary>Why no card could be opened - a key the shell turns into a sentence.</summary>
public sealed class NoCardException(string messageKey, string detail)
    : Exception($"{messageKey}: {detail}")
{
    public string MessageKey { get; } = messageKey;
}

/// <summary>
/// Finding the token among whatever else is plugged into the machine.
/// </summary>
/// <remarks>
/// Here rather than in each shell, because both of them get it wrong the same
/// way: a desk has a badge reader and a token reader, and the token in a
/// contact reader is not called "YubiKey" - it is called after the reader. So
/// the choice is made by asking each card whether it answers the PIV applet,
/// which writes nothing and costs one command.
/// </remarks>
public static class CardAccess
{
    /// <summary>Readers the system knows about, plugged in or not.</summary>
    public static IReadOnlyList<string> Readers()
    {
        if (!PcscContext.IsSupported)
        {
            return [];
        }

        using var context = PcscContext.Establish();

        return context.ListReaders();
    }

    /// <summary>
    /// Opens the token, or says which of the three things went wrong: no
    /// reader, no card, or a card that is not PIV.
    /// </summary>
    /// <param name="preferred">Part of a reader's name, when the operator named one.</param>
    public static CardHandle Open(string? preferred = null)
    {
        if (!PcscContext.IsSupported)
        {
            throw new NoCardException("error.reader.none", "this system has no PC/SC");
        }

        var context = PcscContext.Establish();

        try
        {
            var readers = context.ListReaders();
            if (readers.Count == 0)
            {
                throw new NoCardException("error.reader.none", "no readers at all");
            }

            foreach (var reader in Candidates(readers, preferred))
            {
                var transport = context.Connect(reader);
                if (transport is null)
                {
                    continue;
                }

                var handle = new CardHandle(context, transport, new PivConnection(transport), reader);
                if (handle.Session.Select())
                {
                    return handle;
                }

                // Not this one: a badge, or a card with no PIV applet. Let it
                // go without taking the whole context with it.
                handle.Session.Connection.Dispose();
                transport.Dispose();
            }

            throw new NoCardException("error.card.none",
                $"no card answers PIV in: {string.Join(", ", readers)}");
        }
        catch
        {
            context.Dispose();
            throw;
        }
    }

    private static IEnumerable<string> Candidates(IReadOnlyList<string> readers, string? preferred) =>
        preferred is not null
            ? readers.Where(r => r.Contains(preferred, StringComparison.OrdinalIgnoreCase))
            : readers.OrderByDescending(r => r.Contains("yubikey", StringComparison.OrdinalIgnoreCase));
}

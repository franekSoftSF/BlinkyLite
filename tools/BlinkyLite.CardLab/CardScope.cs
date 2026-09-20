using BlinkyLite.Piv;
using BlinkyLite.Piv.Pcsc;

namespace BlinkyLite.CardLab;

/// <summary>
/// The reader, the card and the PIV session for one run, opened together and
/// closed together.
/// </summary>
/// <remarks>
/// A scope rather than three nested <c>using</c> blocks in every command,
/// because the closing matters as much as the opening: <c>ykman</c> and the
/// Windows minidriver want the same reader, and anything still holding it
/// answers them with SCARD_E_SHARING_VIOLATION. The report is written after
/// this is disposed.
/// </remarks>
internal sealed class CardScope : IDisposable
{
    private readonly PcscContext context;
    private readonly IDisposable transport;
    private readonly PivConnection connection;

    private CardScope(PcscContext context, IDisposable transport, PivConnection connection, string reader)
    {
        this.context = context;
        this.transport = transport;
        this.connection = connection;

        Reader = reader;
        Session = new PivSession(connection);
    }

    public string Reader { get; }

    public PivSession Session { get; }

    /// <summary>Opens the token, or says into the transcript why it could not.</summary>
    public static CardScope? Open(Options options, Transcript log)
    {
        var context = PcscContext.Establish();

        try
        {
            var readers = context.ListReaders();
            if (readers.Count == 0)
            {
                log.Problem("Nie widac zadnego czytnika. Czy klucz jest wlozony?");
                context.Dispose();
                return null;
            }

            foreach (var reader in Candidates(readers, options.Reader))
            {
                // A station has more readers than cards, and an empty one is
                // not an error worth stopping on - it is simply not the one.
                var transport = context.Connect(reader);
                if (transport is null)
                {
                    continue;
                }

                // SELECT decides, not the name: a badge in the first reader and
                // the token in the second is the normal state of a desk, and
                // SELECT is the cheapest question that tells them apart. It
                // writes nothing.
                var scope = new CardScope(context, transport, new PivConnection(transport), reader);
                if (scope.Session.Select())
                {
                    log.Say($"czytnik:      {reader}");
                    return scope;
                }

                scope.connection.Dispose();
                transport.Dispose();
            }

            log.Problem(options.Reader is { } wanted
                ? $"Zaden czytnik pasujacy do \"{wanted}\" nie ma karty PIV. Czytniki: {string.Join(", ", readers)}"
                : $"Zaden czytnik nie ma karty PIV. Czytniki: {string.Join(", ", readers)}");

            context.Dispose();
            return null;
        }
        catch
        {
            context.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Readers worth trying, best first: what the operator named, then anything
    /// calling itself a YubiKey, then the rest.
    /// </summary>
    /// <remarks>
    /// The rest matters. A YubiKey in its own USB reader is called one, but the
    /// same token in a contact reader is called after the reader - on this
    /// bench, an OMNIKEY - and refusing to look there would mean the tool could
    /// not see a card sitting right in front of it.
    /// </remarks>
    private static IEnumerable<string> Candidates(IReadOnlyList<string> readers, string? wanted)
    {
        if (wanted is not null)
        {
            return readers.Where(r => r.Contains(wanted, StringComparison.OrdinalIgnoreCase));
        }

        return readers
            .OrderByDescending(r => r.Contains("yubikey", StringComparison.OrdinalIgnoreCase));
    }

    public void Dispose()
    {
        connection.Dispose();
        transport.Dispose();
        context.Dispose();
    }
}

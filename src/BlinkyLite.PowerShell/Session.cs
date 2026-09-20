using System.Management.Automation;
using BlinkyLite.Contracts;
using BlinkyLite.Issuance;
using BlinkyLite.Issuance.Api;
using BlinkyLite.Issuance.Eobo;

namespace BlinkyLite.PowerShell;

/// <summary>
/// The connection, for as long as the PowerShell session lasts.
/// </summary>
/// <remarks>
/// Static because that is what a shell session is: <c>Connect-BlinkyLite</c>
/// once, then work. The token lives here and in nothing else - not in a file,
/// not in an environment variable, not in a variable the script can print by
/// accident. It expires in thirty minutes.
/// </remarks>
internal static class Session
{
    public static ServerClient? Client { get; private set; }

    public static CurrentUser? User { get; private set; }

    public static Uri? Server { get; private set; }

    public static void Open(ServerClient client, CurrentUser user, Uri server)
    {
        Client?.Dispose();

        Client = client;
        User = user;
        Server = server;
    }

    public static void Close()
    {
        Client?.Dispose();
        Client = null;
        User = null;
        Server = null;
    }

    /// <summary>The client, or a refusal that says to connect first.</summary>
    public static ServerClient Require(Cmdlet cmdlet)
    {
        if (Client is null)
        {
            cmdlet.ThrowTerminatingError(new ErrorRecord(
                new InvalidOperationException(
                    "Nie ma polaczenia z serwerem. Uruchom najpierw Connect-BlinkyLite."),
                "BlinkyLite.NotConnected", ErrorCategory.ConnectionError, null));
        }

        return Client!;
    }
}

/// <summary>
/// Collects the engine's steps so the cmdlet can write them from its own
/// thread.
/// </summary>
/// <remarks>
/// <see cref="Progress{T}"/> cannot be used here. It posts to the captured
/// synchronization context, and a PowerShell pipeline has none, so the
/// callback lands on a thread-pool thread - where <c>WriteVerbose</c> throws
/// <c>PSInvalidOperationException</c> and takes the whole process with it.
/// WPF has a context and hid this; the first run in pwsh did not.
/// </remarks>
internal sealed class StepQueue : IProgress<string>
{
    private readonly System.Collections.Concurrent.ConcurrentQueue<string> steps = new();

    public void Report(string value) => steps.Enqueue(value);

    /// <summary>Everything reported so far, in order, for the caller to write.</summary>
    public IEnumerable<string> Drain()
    {
        while (steps.TryDequeue(out var step))
        {
            yield return step;
        }
    }

    /// <summary>
    /// Waits for the work, writing steps as they arrive - on this thread,
    /// which is the only one allowed to write.
    /// </summary>
    public T Pump<T>(Task<T> work, Action<string> write)
    {
        while (!work.Wait(TimeSpan.FromMilliseconds(100)))
        {
            foreach (var step in Drain())
            {
                write(step);
            }
        }

        foreach (var step in Drain())
        {
            write(step);
        }

        return work.GetAwaiter().GetResult();
    }
}

/// <summary>Turns what the engine throws into something a shell can show.</summary>
internal static class Problems
{
    /// <summary>
    /// The message key in the operator's language, with the technical detail
    /// kept underneath.
    /// </summary>
    /// <remarks>
    /// The server sends keys, never sentences (docs/08). A cmdlet that printed
    /// the raw exception would show English to a Polish operator and an
    /// HRESULT to everybody.
    /// </remarks>
    public static ErrorRecord Of(Exception e)
    {
        var (key, id, category) = e switch
        {
            ServerException server => (server.MessageKey, "BlinkyLite.Server", ErrorCategory.InvalidResult),
            IssuanceFailedException failed => (failed.MessageKey, "BlinkyLite.Issuance", ErrorCategory.InvalidResult),
            PersonalisationRefusedException refused => (refused.MessageKey, "BlinkyLite.Card", ErrorCategory.InvalidOperation),
            NoCardException card => (card.MessageKey, "BlinkyLite.Card", ErrorCategory.DeviceError),
            CertEnrollException => (ErrorCodes.CaCmcFailed, "BlinkyLite.CertEnroll", ErrorCategory.InvalidOperation),
            _ => (ErrorCodes.Internal, "BlinkyLite.Unexpected", ErrorCategory.NotSpecified),
        };

        return new ErrorRecord(
            new InvalidOperationException($"{Strings.Current[key]} ({e.Message})", e),
            id, category, null);
    }
}

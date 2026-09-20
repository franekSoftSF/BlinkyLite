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

using System.Management.Automation.Host;
using System.Net;
using System.Security;
using BlinkyLite.Contracts;
using BlinkyLite.Issuance;

namespace BlinkyLite.PowerShell;

/// <summary>
/// Asks the person the key is for to type their PIN into the host, twice.
/// </summary>
/// <remarks>
/// <para>
/// Through <see cref="PSHostUserInterface.ReadLineAsSecureString"/>, so the
/// characters are not echoed and do not reach the transcript, the history or
/// the screen behind somebody's shoulder.
/// </para>
/// <para>
/// A <see cref="SecureString"/> is not a guarantee of anything on its own -
/// the engine needs the characters and the card needs them in the clear - but
/// it does keep the PIN out of the places PowerShell copies strings into. The
/// value is unprotected at the last possible moment and the memory is freed
/// immediately.
/// </para>
/// </remarks>
internal sealed class HostPinPrompt(PSHost host) : IPinPrompt
{
    public Task<string?> AskAsync(PinPromptContext context, CancellationToken ct = default)
    {
        var ui = host.UI;

        if (context.RefusalKey is { } refusal)
        {
            ui.WriteWarningLine(Strings.Current[refusal]);
        }
        else
        {
            ui.WriteLine();
            ui.WriteLine($"{Strings.Current["pin.title"]} ({context.Serial})");
            ui.WriteLine(Strings.Current["pin.explain"]);
            ui.WriteLine(Strings.Current["pin.rule.length"]);
        }

        ui.Write($"{Strings.Current["pin.new"]}: ");
        var first = Read(ui);

        ui.Write($"{Strings.Current["pin.repeat"]}: ");
        var second = Read(ui);

        if (first is null || second is null)
        {
            return Task.FromResult<string?>(null);
        }

        if (!string.Equals(first, second, StringComparison.Ordinal))
        {
            ui.WriteWarningLine(Strings.Current["pin.rule.mismatch"]);

            // Not an attempt as the engine counts them: it judges PINs, and it
            // has not been shown one.
            return AskAsync(context, ct);
        }

        return Task.FromResult<string?>(first);
    }

    private static string? Read(PSHostUserInterface ui)
    {
        var secure = ui.ReadLineAsSecureString();
        ui.WriteLine();

        if (secure is null || secure.Length == 0)
        {
            return null;
        }

        try
        {
            return new NetworkCredential(string.Empty, secure).Password;
        }
        finally
        {
            secure.Dispose();
        }
    }
}

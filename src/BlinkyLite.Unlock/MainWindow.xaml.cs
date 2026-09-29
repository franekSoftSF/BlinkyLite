using System.Globalization;
using System.Net.Http;
using System.Windows;
using System.Windows.Controls;
using BlinkyLite.Contracts;
using BlinkyLite.Piv;
using BlinkyLite.Ui.Localisation;
using BlinkyLite.Ui.Theme;
using Serilog;

namespace BlinkyLite.Unlock;

/// <summary>
/// Unblocking a PIN, in one window: with the PUK in hand (0056, D-36) or over
/// the telephone (0057, D-37).
/// </summary>
/// <remarks>
/// <para>
/// Either way the PUK is only ever characters passing through this process on
/// their way to the card. It is not written to the log, not to a field, not to
/// a file - and in the telephone mode it is not shown either, not even to the
/// person holding the key. Same for the PIN.
/// </para>
/// <para>
/// No sign-in in either mode, on purpose: somebody whose PIN is blocked cannot
/// authenticate to anything, so asking them to would be asking for the one
/// thing they do not have. What stands in for it over the telephone is a code
/// they read out and somebody with a role approves.
/// </para>
/// </remarks>
public partial class MainWindow : Window
{
    private uint? serial;
    private bool pukAvailable;

    private bool remote;
    private RemoteUnlock? server;
    private UnlockTicket? ticket;
    private CancellationTokenSource? waiting;

    public MainWindow()
    {
        InitializeComponent();

        LanguagePicker.ItemsSource = Text.Languages.Select(l => new { l.Code, l.Name }).ToList();
        LanguagePicker.SelectedIndex = Strings.Supported
            .ToList()
            .IndexOf(Strings.Current.Culture.TwoLetterISOLanguageName);

        ServerBox.Text = App.Settings.Server ?? "https://";

        Loaded += (_, _) =>
        {
            Mode(remote: false);
            ReadCard();
            PukBox.Focus();
        };

        // A window that closes while a request is in flight should stop asking,
        // not keep a thread alive to answer nobody.
        Closed += (_, _) => Forget();
    }

    private void CardRefreshed(object sender, RoutedEventArgs e) => ReadCard();

    /// <summary>What is in the reader, and whether this window can help with it.</summary>
    private void ReadCard()
    {
        serial = null;
        pukAvailable = false;

        try
        {
            using var card = CardAccess.Open();
            var inventory = card.Session.ReadInventory(includeCertificates: false);

            serial = inventory.SerialNumber;
            pukAvailable = inventory.Puk.State != PinState.Blocked && !inventory.Puk.IsUnrecoverable;

            var state = inventory.Pin.State == PinState.Blocked ? "unlock.card.pin-blocked" : "unlock.card.pin-ok";
            CardText.Text = Text.Of(state, serial?.ToString(CultureInfo.InvariantCulture) ?? "?")
                            + (inventory.Puk.RemainingRetries is { } left
                                ? " · " + Text.Of("unlock.card.puk-left", left)
                                : "");

            if (inventory.Puk.IsUnrecoverable)
            {
                // A Bio token has no PUK at all; nothing here can help it.
                CardText.Text = Text.Of("unlock.card.no-puk");
            }
            else if (inventory.Puk.State == PinState.Blocked)
            {
                CardText.Text = Text.Of("unlock.card.puk-blocked");
            }

            Paint(CardMark, pukAvailable ? "AccentText" : "Danger");
            Log.Information("Card {Serial}: PIN {Pin}, PUK {Puk} ({Left} left)",
                serial, inventory.Pin.State, inventory.Puk.State, inventory.Puk.RemainingRetries);
        }
        catch (NoCardException e)
        {
            CardText.Text = Text.Of(e.MessageKey);
            Paint(CardMark, "MutedText");
        }
        catch (PivException e)
        {
            CardText.Text = Text.Of(ErrorCodes.CardNotAYubiKey);
            Say(e);
        }

        Typed(this, new RoutedEventArgs());
    }

    private void PukModeChosen(object sender, RoutedEventArgs e) => Mode(remote: false);

    private void RemoteModeChosen(object sender, RoutedEventArgs e) => Mode(remote: true);

    /// <summary>
    /// Which of the two ways the PUK arrives. Switching away from a code that
    /// is waiting drops it: the person told the helpdesk they would do it the
    /// other way, and a code nobody watches is worse than none.
    /// </summary>
    private void Mode(bool remote)
    {
        this.remote = remote;
        Forget();

        PukPanel.Visibility = remote ? Visibility.Collapsed : Visibility.Visible;
        RemotePanel.Visibility = remote ? Visibility.Visible : Visibility.Collapsed;
        CodePanel.Visibility = Visibility.Collapsed;

        // Not null for the one that is off: a Style set to null blocks the
        // implicit Button style as well, and the button loses its look.
        var chosen = (Style)FindResource("Primary");
        var plain = (Style)FindResource(typeof(Button));
        PukModeButton.Style = remote ? plain : chosen;
        RemoteModeButton.Style = remote ? chosen : plain;

        UnlockButtonText.Text = Text.Of(remote ? "unlock.remote.start" : "unlock.action");
        Message.Text = Details.Text = "";

        if (remote)
        {
            ServerBox.Focus();
        }
        else
        {
            PukBox.Clear();
            PukBox.Focus();
        }

        Typed(this, new RoutedEventArgs());
    }

    /// <summary>Checked as the person types, and again by the card when they press the button.</summary>
    private void Typed(object sender, RoutedEventArgs e)
    {
        var puk = PukBox.Password;
        var pin = PinBox.Password;
        var repeat = RepeatBox.Password;

        UnlockButton.IsEnabled = false;

        // While a code is waiting for somebody to approve it there is nothing
        // to press: the answer arrives by itself.
        if (ticket is not null)
        {
            return;
        }

        if (remote && !Uri.TryCreate(ServerBox.Text.Trim(), UriKind.Absolute, out _))
        {
            Show("unlock.remote.server", acceptable: false);
            return;
        }

        if (!remote && puk.Length < 4)
        {
            Show("unlock.puk.needed", acceptable: false);
            return;
        }

        // In the typed mode the PUK is in hand, so the rules can also refuse a
        // PIN equal to it: unblocking to the value that was just read out over
        // the telephone is not unblocking. Over the telephone nobody here knows
        // the PUK yet, so that one check waits until it arrives.
        var verdict = PinRules.Check(pin, PinComplexityPolicy.Default, serial, remote ? null : puk);
        if (!verdict.IsAcceptable)
        {
            Show(verdict.MessageKey, acceptable: false);
            return;
        }

        if (repeat.Length == 0)
        {
            Show("pin.repeat", acceptable: false);
            return;
        }

        var same = string.Equals(pin, repeat, StringComparison.Ordinal);
        Show(same ? "pin.rule.ok" : "pin.rule.mismatch", same);
        UnlockButton.IsEnabled = same && pukAvailable;
    }

    private async void Unlocked(object sender, RoutedEventArgs e)
    {
        if (remote)
        {
            await AskAsync();
            return;
        }

        UnlockButton.IsEnabled = false;
        Message.Text = Details.Text = "";

        await UnblockAsync(PukBox.Password, PinBox.Password, report: null);
    }

    /// <summary>
    /// The telephone mode: ask for a code, show it, and then wait. Nothing else
    /// happens here until somebody with a role approves that one request.
    /// </summary>
    private async Task AskAsync()
    {
        UnlockButton.IsEnabled = false;
        Message.Text = Details.Text = "";

        if (serial is not { } cardSerial)
        {
            Say(Text.Of("error.card.none"), problem: true);
            return;
        }

        var address = ServerBox.Text.Trim();
        if (!Uri.TryCreate(address, UriKind.Absolute, out var uri))
        {
            Say(Text.Of(ErrorCodes.UnlockNoServer), problem: true);
            return;
        }

        try
        {
            server = new RemoteUnlock(uri);
            ticket = await server.AskAsync(cardSerial, Environment.MachineName, CancellationToken.None);

            // Remembered so that the next person at this workstation does not
            // have to know the address either.
            App.Settings = App.Settings with { Server = address };
            App.Settings.Save();

            CodePanel.Visibility = Visibility.Visible;
            CodeText.Text = ticket.Code;
            ExpiryText.Text = Text.Of("unlock.remote.expires", ticket.ExpiresAt.ToLocalTime().ToString("t", CultureInfo.CurrentCulture));
            WaitingText.Text = Text.Of("unlock.remote.waiting");
            Log.Information("Unlock request {Request} for card {Serial}", ticket.RequestId, cardSerial);

            waiting = new CancellationTokenSource();
            await WaitAsync(waiting.Token);
        }
        catch (RemoteRefusedException refused)
        {
            Forget();
            Say(Text.Of(refused.MessageKey), problem: true);
            Log.Warning(refused, "Server refused the unlock request");
        }
        catch (Exception unreachable) when (unreachable is HttpRequestException or TaskCanceledException)
        {
            Forget();
            Say(Text.Of(ErrorCodes.UnlockNoServer), problem: true);
            Log.Warning(unreachable, "Server unreachable at {Address}", address);
        }
    }

    /// <summary>
    /// Asks the same question until it gets an answer other than "still
    /// waiting". The PUK arrives on exactly one of these calls, is used, and is
    /// gone - a second call would be told the request was already delivered.
    /// </summary>
    private async Task WaitAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested && ticket is { } current && server is { } client)
        {
            try
            {
                await Task.Delay(RemoteUnlock.PollInterval, ct);
            }
            catch (OperationCanceledException)
            {
                // The window closed, or the person switched to typing the PUK.
                return;
            }

            UnlockState state;
            try
            {
                state = await client.StateAsync(current, ct);
            }
            catch (Exception problem) when (problem is HttpRequestException or TaskCanceledException)
            {
                // A helpdesk call outlasts a dropped Wi-Fi connection; keep
                // asking rather than throwing the code away.
                Log.Debug(problem, "Unlock request {Request}: the server did not answer this time", current.RequestId);
                continue;
            }

            switch (state.State)
            {
                case UnlockStates.Pending or UnlockStates.Approved:
                    continue;

                case UnlockStates.Delivered when state.Puk is { Length: > 0 } puk:
                    Forget(keepClient: true);
                    WaitingText.Text = Text.Of("unlock.remote.approved");
                    await UseAsync(current, puk);
                    return;

                case UnlockStates.Refused:
                    Finish(ErrorCodes.UnlockRefused);
                    return;

                case UnlockStates.Expired:
                    Finish(ErrorCodes.UnlockExpired);
                    return;

                default:
                    // Delivered without a PUK: this request was collected once
                    // already, and once is all there is.
                    Finish(ErrorCodes.UnlockInvalidState);
                    return;
            }
        }
    }

    /// <summary>The PUK that just arrived, on the card, and then the report.</summary>
    private async Task UseAsync(UnlockTicket current, string puk)
    {
        var pin = PinBox.Password;

        // The one check that could not be made before the PUK was known.
        var verdict = PinRules.Check(pin, PinComplexityPolicy.Default, serial, puk);
        if (!verdict.IsAcceptable)
        {
            Show(verdict.MessageKey, acceptable: false);
            await ReportAsync(current, ok: false, verdict.MessageKey);
            Forget();
            return;
        }

        await UnblockAsync(puk, pin, current);
    }

    /// <summary>
    /// The card half, the same for both modes. <paramref name="report"/> is the
    /// request to tell the server about afterwards, or null when nobody asked.
    /// </summary>
    private async Task UnblockAsync(string puk, string pin, UnlockTicket? report)
    {
        try
        {
            // On a background thread: this talks to a card over PC/SC.
            await Task.Run(() =>
            {
                using var card = CardAccess.Open();
                card.Session.UnblockPin(puk, pin);
            });

            // Neither value is kept for a second try: a wrong PUK is typed
            // again, and a PIN nobody remembers is worse than one box to fill.
            Clear();
            Say("unlock.done", problem: false);
            Log.Information("PIN unblocked on card {Serial}", serial);

            if (report is not null)
            {
                await ReportAsync(report, ok: true, null);
            }

            ReadCard();
        }
        catch (PivVerificationFailedException wrong)
        {
            PukBox.Clear();
            Say(Text.Of("unlock.puk.wrong", wrong.RetriesLeft), problem: true);
            Log.Warning("Wrong PUK on card {Serial}, {Left} attempts left", serial, wrong.RetriesLeft);
            await ReportAsync(report, ok: false, "unlock.puk.wrong");
            ReadCard();
        }
        catch (PivAuthenticationBlockedException blocked)
        {
            Clear();
            Say(Text.Of("unlock.card.puk-blocked"), problem: true);
            Log.Warning(blocked, "PUK blocked on card {Serial}", serial);
            await ReportAsync(report, ok: false, "unlock.card.puk-blocked");
            ReadCard();
        }
        catch (Exception problem) when (problem is PivException or NoCardException)
        {
            Say(problem);
            await ReportAsync(report, ok: false, problem.GetType().Name);
            ReadCard();
        }
        finally
        {
            puk = pin = null!;
            Forget();
            UnlockButton.IsEnabled = false;
        }
    }

    /// <summary>
    /// A message key or a class name, never a PUK, a PIN or a serial: this ends
    /// up in the audit trail, which the person who called does not read.
    /// </summary>
    private async Task ReportAsync(UnlockTicket? report, bool ok, string? error)
    {
        if (report is null || server is null)
        {
            return;
        }

        try
        {
            await server.ReportAsync(report, ok, error, CancellationToken.None);
        }
        catch (Exception problem) when (problem is RemoteRefusedException or HttpRequestException or TaskCanceledException)
        {
            // The card is already unblocked or already refused; a report that
            // did not arrive is worth a line in the log and nothing more.
            Log.Warning(problem, "Could not report the result of unlock request {Request}", report.RequestId);
        }
    }

    private void Finish(string messageKey)
    {
        Forget();
        CodePanel.Visibility = Visibility.Collapsed;
        Say(Text.Of(messageKey), problem: true);
        Typed(this, new RoutedEventArgs());
    }

    /// <summary>Stops waiting and drops the ticket, the secret in it and the connection.</summary>
    private void Forget(bool keepClient = false)
    {
        waiting?.Cancel();
        waiting?.Dispose();
        waiting = null;
        ticket = null;

        if (!keepClient)
        {
            server?.Dispose();
            server = null;
        }
    }

    private void Clear()
    {
        PukBox.Clear();
        PinBox.Clear();
        RepeatBox.Clear();
    }

    private void Show(string messageKey, bool acceptable)
    {
        Verdict.Text = Text.Of(messageKey);
        Verdict.SetResourceReference(ForegroundProperty, acceptable ? "AccentText" : "MutedText");
        Mark.Text = acceptable ? "✓" : "•";
        Mark.SetResourceReference(ForegroundProperty, acceptable ? "AccentText" : "MutedText");
    }

    private static void Paint(TextBlock block, string role) =>
        block.SetResourceReference(ForegroundProperty, role);

    private void Say(string messageKeyOrText, bool problem)
    {
        Message.Text = messageKeyOrText.Contains(' ', StringComparison.Ordinal)
            ? messageKeyOrText
            : Text.Of(messageKeyOrText);
        Message.SetResourceReference(ForegroundProperty, problem ? "Danger" : "AccentText");
        Details.Text = "";
        Log.Information("{Message}", Message.Text);
    }

    private void Say(Exception e)
    {
        Message.Text = e switch
        {
            NoCardException card => Text.Of(card.MessageKey),
            _ => Text.Of(ErrorCodes.Internal),
        };
        Message.SetResourceReference(ForegroundProperty, "Danger");

        // The technical line stays as it came: it is what somebody pastes into
        // a message to the administrator.
        Details.Text = e.Message;
        Log.Error(e, "{Message}", Message.Text);
    }

    private void ThemeToggled(object sender, RoutedEventArgs e) => ThemeManager.Toggle();

    private void LanguageChanged(object sender, SelectionChangedEventArgs e)
    {
        if (LanguagePicker.SelectedItem is null)
        {
            return;
        }

        var code = (string)LanguagePicker.SelectedItem.GetType().GetProperty("Code")!.GetValue(LanguagePicker.SelectedItem)!;
        Strings.Current.Culture = CultureInfo.GetCultureInfo(code);

        // Written by this code, so it does not follow the binding.
        UnlockButtonText.Text = Text.Of(remote ? "unlock.remote.start" : "unlock.action");
        if (ticket is not null)
        {
            WaitingText.Text = Text.Of("unlock.remote.waiting");
            ExpiryText.Text = Text.Of("unlock.remote.expires",
                ticket.ExpiresAt.ToLocalTime().ToString("t", CultureInfo.CurrentCulture));
        }

        ReadCard();
    }
}

using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using BlinkyLite.Contracts;
using BlinkyLite.Piv;
using BlinkyLite.Ui.Localisation;
using BlinkyLite.Ui.Theme;
using Serilog;

namespace BlinkyLite.Unlock;

/// <summary>
/// Unblocking a PIN with the PUK, in one window (0056, D-36).
/// </summary>
/// <remarks>
/// <para>
/// The PUK comes from the helpdesk, who read it from the console - which is
/// where the disclosure is audited, with a reason. This window sees it only
/// as characters in a box and never writes it anywhere: not to the log, not
/// to a field, not to a file. Same for the PIN.
/// </para>
/// <para>
/// No server and no sign-in on purpose: somebody whose PIN is blocked cannot
/// authenticate to anything, so asking them to would be asking for the one
/// thing they do not have.
/// </para>
/// </remarks>
public partial class MainWindow : Window
{
    private uint? serial;
    private bool pukAvailable;

    public MainWindow()
    {
        InitializeComponent();

        LanguagePicker.ItemsSource = Text.Languages.Select(l => new { l.Code, l.Name }).ToList();
        LanguagePicker.SelectedIndex = Strings.Supported
            .ToList()
            .IndexOf(Strings.Current.Culture.TwoLetterISOLanguageName);

        Loaded += (_, _) =>
        {
            ReadCard();
            PukBox.Focus();
        };
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

    /// <summary>Checked as the person types, and again by the card when they press the button.</summary>
    private void Typed(object sender, RoutedEventArgs e)
    {
        var puk = PukBox.Password;
        var pin = PinBox.Password;
        var repeat = RepeatBox.Password;

        if (puk.Length < 4)
        {
            Show("unlock.puk.needed", acceptable: false);
            UnlockButton.IsEnabled = false;
            return;
        }

        // The PUK is in hand here, so the rules can refuse a PIN equal to it:
        // unblocking to the value that was just handed out over the phone is
        // not unblocking.
        var verdict = PinRules.Check(pin, PinComplexityPolicy.Default, serial, puk);
        if (!verdict.IsAcceptable)
        {
            Show(verdict.MessageKey, acceptable: false);
            UnlockButton.IsEnabled = false;
            return;
        }

        if (repeat.Length == 0)
        {
            Show("pin.repeat", acceptable: false);
            UnlockButton.IsEnabled = false;
            return;
        }

        var same = string.Equals(pin, repeat, StringComparison.Ordinal);
        Show(same ? "pin.rule.ok" : "pin.rule.mismatch", same);
        UnlockButton.IsEnabled = same && pukAvailable;
    }

    private async void Unlocked(object sender, RoutedEventArgs e)
    {
        UnlockButton.IsEnabled = false;
        Message.Text = Details.Text = "";

        var puk = PukBox.Password;
        var pin = PinBox.Password;

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
            ReadCard();
        }
        catch (PivVerificationFailedException wrong)
        {
            PukBox.Clear();
            Say(Text.Of("unlock.puk.wrong", wrong.RetriesLeft), problem: true);
            Log.Warning("Wrong PUK on card {Serial}, {Left} attempts left", serial, wrong.RetriesLeft);
            ReadCard();
        }
        catch (PivAuthenticationBlockedException blocked)
        {
            Clear();
            Say(Text.Of("unlock.card.puk-blocked"), problem: true);
            Log.Warning(blocked, "PUK blocked on card {Serial}", serial);
            ReadCard();
        }
        catch (Exception problem) when (problem is PivException or NoCardException)
        {
            Say(problem);
            ReadCard();
        }
        finally
        {
            puk = pin = null!;
            UnlockButton.IsEnabled = false;
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

    private static void Paint(System.Windows.Controls.TextBlock block, string role) =>
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
        ReadCard();
    }
}

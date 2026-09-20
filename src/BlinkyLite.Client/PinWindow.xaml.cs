using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using BlinkyLite.Client.Localisation;
using BlinkyLite.Contracts;
using BlinkyLite.Issuance;

namespace BlinkyLite.Client;

/// <summary>
/// Where the person the key is for types their PIN, twice.
/// </summary>
/// <remarks>
/// <para>
/// The PIN exists in this window and in the card, and nowhere else: not in the
/// view model, not in a property, not in the log. It is read out of the
/// <see cref="PasswordBox"/> at the moment it is handed to the engine, and the
/// boxes are cleared when the window closes.
/// </para>
/// <para>
/// The rules are checked here as the person types, and again by the engine
/// before the card is touched. That is not a duplicate: this one is so that
/// somebody learns what is wrong while they can still fix it, and the engine's
/// is the one that decides.
/// </para>
/// </remarks>
public partial class PinWindow : Window
{
    private readonly PinPromptContext context;

    public PinWindow(PinPromptContext context)
    {
        this.context = context;

        InitializeComponent();

        LanguagePicker.ItemsSource = Text.Languages.Select(l => new { l.Code, l.Name }).ToList();
        LanguagePicker.SelectedIndex = Strings.Supported
            .ToList()
            .IndexOf(Strings.Current.Culture.TwoLetterISOLanguageName);

        if (context.RefusalKey is { } refusal)
        {
            Show(refusal, acceptable: false);
            Attempts.Text = Text.Of("pin.attempts-left", Attempts_Left());
        }
        else
        {
            Show("pin.rule.length", acceptable: false);
        }

        Loaded += (_, _) => First.Focus();
        Closed += (_, _) => Clear();
    }

    /// <summary>The PIN, or null when the person gave up. Read once.</summary>
    public string? Pin { get; private set; }

    private int Attempts_Left() => Math.Max(0, 3 - context.Attempt + 1);

    private void Typed(object sender, RoutedEventArgs e)
    {
        var first = First.Password;
        var second = Second.Password;

        var verdict = PinRules.Check(first, context.Policy, context.Serial);

        if (!verdict.IsAcceptable)
        {
            Show(verdict.MessageKey, acceptable: false);
            Accept.IsEnabled = false;
            return;
        }

        if (second.Length == 0)
        {
            Show("pin.repeat", acceptable: false);
            Accept.IsEnabled = false;
            return;
        }

        var same = string.Equals(first, second, StringComparison.Ordinal);

        Show(same ? "pin.rule.ok" : "pin.rule.mismatch", same);
        Accept.IsEnabled = same;
    }

    private void Show(string messageKey, bool acceptable)
    {
        Verdict.Text = Text.Of(messageKey);
        Verdict.SetResourceReference(ForegroundProperty, acceptable ? "AccentText" : "MutedText");

        // The mark carries the same answer as the colour, for whoever does not
        // see the colour.
        Mark.Text = acceptable ? "✓" : "•";
        Mark.SetResourceReference(ForegroundProperty, acceptable ? "AccentText" : "MutedText");
    }

    private void LanguageChanged(object sender, SelectionChangedEventArgs e)
    {
        if (LanguagePicker.SelectedItem is null)
        {
            return;
        }

        var code = (string)LanguagePicker.SelectedItem.GetType().GetProperty("Code")!.GetValue(LanguagePicker.SelectedItem)!;
        Strings.Current.Culture = CultureInfo.GetCultureInfo(code);

        // The verdict is text this window wrote, so it does not follow the
        // binding; it is written again in the new language.
        Typed(sender, e);
    }

    private void Accepted(object sender, RoutedEventArgs e)
    {
        Pin = First.Password;
        DialogResult = true;
    }

    private void Cancelled(object sender, RoutedEventArgs e)
    {
        Pin = null;
        DialogResult = false;
    }

    private void Clear()
    {
        First.Clear();
        Second.Clear();
    }
}

/// <summary>Shows <see cref="PinWindow"/> when the engine asks for a PIN.</summary>
/// <remarks>
/// The engine is on a background thread - it is talking to a card - so the
/// window is opened on the UI thread and awaited from there.
/// </remarks>
public sealed class WindowPinPrompt(Window owner) : IPinPrompt
{
    public Task<string?> AskAsync(PinPromptContext context, CancellationToken ct = default) =>
        owner.Dispatcher.InvokeAsync(() =>
        {
            var window = new PinWindow(context) { Owner = owner };

            return window.ShowDialog() == true ? window.Pin : null;
        }).Task;
}

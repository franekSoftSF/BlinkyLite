using System.Collections.ObjectModel;
using System.Globalization;
using System.Net.Http;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using BlinkyLite.Client.Localisation;
using BlinkyLite.Client.Theme;
using BlinkyLite.Contracts;
using BlinkyLite.Issuance;
using BlinkyLite.Issuance.Api;
using BlinkyLite.Issuance.Eobo;

namespace BlinkyLite.Client;

/// <summary>One step of an issuance, as the window shows it.</summary>
/// <remarks>
/// The mark is here as well as the colour, because at this desk somebody may
/// not tell red from green, and the state of a card is not a thing to guess.
/// </remarks>
public sealed class Step(string text, string mark, Brush colour)
{
    public string Text { get; } = text;

    public string Mark { get; } = mark;

    public Brush Colour { get; } = colour;
}

/// <summary>
/// The operator's window: sign in, choose a person and a profile, issue.
/// </summary>
/// <remarks>
/// It collects input and shows progress. Every decision about a card or a CA
/// is in <see cref="IssuanceRunner"/>, which is the same engine the station
/// tool and (later) the PowerShell module drive - if an <c>if</c> about a card
/// appeared here, it would be in the wrong place.
/// </remarks>
public partial class MainWindow : Window
{
    private readonly ObservableCollection<Step> steps = [];

    private ServerClient? server;
    private DirectoryUser? target;

    public MainWindow()
    {
        InitializeComponent();

        Steps.ItemsSource = steps;
        LanguagePicker.ItemsSource = Text.Languages.Select(l => new { l.Code, l.Name }).ToList();
        LanguagePicker.SelectedIndex = Strings.Supported
            .ToList()
            .IndexOf(Strings.Current.Culture.TwoLetterISOLanguageName);

        ServerBox.Text = "https://";
        UserBox.Text = Environment.UserDomainName + "\\" + Environment.UserName;
    }

    private async void SignedIn(object sender, RoutedEventArgs e)
    {
        if (!Uri.TryCreate(ServerBox.Text.Trim(), UriKind.Absolute, out var address))
        {
            Say(ErrorCodes.BadRequest, problem: true);
            return;
        }

        SignInButton.IsEnabled = false;

        try
        {
            var client = new ServerClient(address);

            // The password is read here and let go of immediately; it is not
            // kept in a field, a property or a view model.
            var user = await client.LoginAsync(UserBox.Text.Trim(), PasswordBox.Password);
            PasswordBox.Clear();

            server = client;
            Who.Text = $"{user.Upn} — {string.Join(", ", user.Roles)}";
            SignIn.Visibility = Visibility.Collapsed;
            Issuing.Visibility = Visibility.Visible;
            SignOutButton.Visibility = Visibility.Visible;

            ProfilePicker.ItemsSource = await client.ProfilesAsync();
            ProfilePicker.SelectedIndex = 0;

            ReadCard();
        }
        catch (Exception problem) when (problem is ServerException or HttpRequestException)
        {
            Say(Explain(problem), problem: true);
        }
        finally
        {
            SignInButton.IsEnabled = true;
        }
    }

    private void SignedOut(object sender, RoutedEventArgs e)
    {
        server?.Dispose();
        server = null;
        target = null;

        steps.Clear();
        People.ItemsSource = null;
        Who.Text = "";
        Message.Text = "";

        Issuing.Visibility = Visibility.Collapsed;
        SignOutButton.Visibility = Visibility.Collapsed;
        SignIn.Visibility = Visibility.Visible;
    }

    private void SearchKey(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            Searched(sender, e);
        }
    }

    private async void Searched(object sender, RoutedEventArgs e)
    {
        if (server is null || SearchBox.Text.Trim().Length < 2)
        {
            Say(ErrorCodes.QueryTooShort, problem: true);
            return;
        }

        try
        {
            People.ItemsSource = await server.SearchAsync(SearchBox.Text.Trim());
            Message.Text = "";
        }
        catch (Exception problem) when (problem is ServerException or HttpRequestException)
        {
            Say(Explain(problem), problem: true);
        }
    }

    private void PersonChosen(object sender, SelectionChangedEventArgs e)
    {
        target = People.SelectedItem as DirectoryUser;
        IssueButton.IsEnabled = target is not null && ProfilePicker.SelectedItem is not null;
    }

    private void CardRefreshed(object sender, RoutedEventArgs e) => ReadCard();

    /// <summary>Reads the token in the reader, writing nothing to it.</summary>
    private void ReadCard()
    {
        try
        {
            using var card = CardAccess.Open();
            var inventory = card.Session.ReadInventory(includeCertificates: false);

            CardText.Text = $"{inventory.SerialNumber}  ·  {inventory.Firmware}  ·  "
                            + $"{inventory.ManagementKey?.Algorithm}  ·  PIN {inventory.Pin.State}";
        }
        catch (NoCardException e)
        {
            CardText.Text = Text.Of(e.MessageKey);
        }
    }

    private async void Issued(object sender, RoutedEventArgs e)
    {
        if (server is null || target is null || ProfilePicker.SelectedItem is not IssuanceProfile profile)
        {
            return;
        }

        var agent = EnrolmentAgent.Find().FirstOrDefault(a => a.IsUsable);
        if (agent is null)
        {
            Say(ErrorCodes.AgentMissing, problem: true);
            return;
        }

        IssueButton.IsEnabled = false;
        steps.Clear();
        Message.Text = "";

        var progress = new Progress<string>(key => steps.Add(
            new Step(Text.Of(key), "•", (Brush)Resources["MutedText"])));

        try
        {
            // On a background thread: this talks to a card, to a server and to
            // a CA, and the window has to stay alive to show the PIN prompt.
            var outcome = await Task.Run(async () =>
            {
                using var card = CardAccess.Open();

                return await new IssuanceRunner(server).RunAsync(
                    card.Session, target, profile, agent.Certificate,
                    new WindowPinPrompt(this), Environment.MachineName, progress);
            });

            if (outcome.IsComplete)
            {
                steps.Add(new Step(Text.Of("client.result.issued"), "✓", (Brush)Resources["AccentText"]));
                Say("client.result.issued", problem: false);
            }
            else if (outcome.PendingAtCa)
            {
                steps.Add(new Step(Text.Of("client.result.pending"), "⧗", (Brush)Resources["Warning"]));
                Say("client.result.pending", problem: false);
            }
        }
        catch (Exception problem)
        {
            steps.Add(new Step(Explain(problem, translated: true), "✗", (Brush)Resources["Danger"]));
            Say(Explain(problem), problem: true);
        }
        finally
        {
            IssueButton.IsEnabled = true;
            ReadCard();
        }
    }

    private void ThemeToggled(object sender, RoutedEventArgs e) => ThemeManager.Toggle();

    private void LanguageChanged(object sender, SelectionChangedEventArgs e)
    {
        if (LanguagePicker.SelectedItem is null)
        {
            return;
        }

        var code = (string)LanguagePicker.SelectedItem.GetType()
            .GetProperty("Code")!.GetValue(LanguagePicker.SelectedItem)!;

        Strings.Current.Culture = CultureInfo.GetCultureInfo(code);
    }

    private void Say(string messageKey, bool problem)
    {
        Message.Text = Text.Of(messageKey);
        Message.SetResourceReference(ForegroundProperty, problem ? "Danger" : "AccentText");
    }

    /// <summary>The message key behind a failure, or the key plus its detail.</summary>
    private static string Explain(Exception e, bool translated = false) => e switch
    {
        ServerException server => translated ? Text.Of(server.MessageKey) : server.MessageKey,
        IssuanceFailedException failed => translated ? Text.Of(failed.MessageKey) : failed.MessageKey,
        PersonalisationRefusedException refused => translated ? Text.Of(refused.MessageKey) : refused.MessageKey,
        NoCardException card => translated ? Text.Of(card.MessageKey) : card.MessageKey,
        CertEnrollException => translated ? Text.Of(ErrorCodes.CaCmcFailed) : ErrorCodes.CaCmcFailed,
        _ => translated ? e.Message : ErrorCodes.Internal,
    };
}

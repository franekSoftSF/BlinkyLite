using System.Security.Cryptography.X509Certificates;
using System.Text;
using BlinkyLite.Contracts;
using BlinkyLite.Issuance;
using BlinkyLite.Issuance.Api;
using BlinkyLite.Issuance.Eobo;

namespace BlinkyLite.CardLab;

/// <summary>
/// One real issuance, end to end: server, card, CA, card, server.
/// </summary>
/// <remarks>
/// The station tool drives the engine and shows what it is doing; it decides
/// nothing about a card or a CA. That is the same division the WPF client will
/// have (0023), which is why this file contains no rule - only prompts,
/// printing and the order of the questions.
/// </remarks>
internal static class Issue
{
    public static async Task<int> RunAsync(Options options, Transcript log)
    {
        if (options.Server is not { } address || !Uri.TryCreate(address, UriKind.Absolute, out var server))
        {
            log.Problem("Podaj --server https://blinkylite.example");
            return 2;
        }

        if (options.Operator is not { } operatorName)
        {
            log.Problem("Podaj --operator DOMENA\\uzytkownik - konto, ktorym logujesz sie do BlinkyLite.");
            return 2;
        }

        if (options.Target is not { } wanted)
        {
            log.Problem("Podaj --target <fragment nazwy> - osobe, dla ktorej jest ta karta.");
            return 2;
        }

        var agent = Agent(options, log);
        if (agent is null)
        {
            return 3;
        }

        using var client = new ServerClient(server);

        // The operator's own AD password, read here and sent over TLS to their
        // own server. It is not stored, not logged and not passed on.
        var password = ConsolePinPrompt.ReadHidden($"Haslo AD dla {operatorName}: ");
        if (password is null)
        {
            log.Problem("Przerwano.");
            return 2;
        }

        CurrentUser user;
        try
        {
            user = await client.LoginAsync(operatorName, password, refusal =>
            {
                if (refusal is not null)
                {
                    log.Problem(Strings.Current[refusal]);
                }

                Console.Write($"{Strings.Current["client.totp.prompt"]}: ");
                return Console.ReadLine();
            }, CancellationToken.None);
        }
        catch (Exception e) when (e is ServerException or HttpRequestException or OperationCanceledException)
        {
            log.Problem($"Logowanie nie przeszlo: {Explain(e)}", e);
            return 4;
        }
        finally
        {
            password = null;
        }

        log.Say($"zalogowany:   {user.Upn}, role: {string.Join(", ", user.Roles)}");
        if (client.BackupCodesLeft is { } left)
        {
            log.Say(Strings.Current.Format("client.totp.backup-left", left));
        }

        try
        {
            var profile = await ChooseProfileAsync(client, options, log);
            var target = await ChooseTargetAsync(client, wanted, log);

            if (profile is null || target is null)
            {
                return 4;
            }

            log.Say(string.Empty);
            log.Say($"karta dla:    {target.DisplayName} ({target.SamAccount})");
            log.Say($"profil:       {profile.Name} -> szablon {profile.Template}");
            log.Say($"CA:           {profile.CaConfig}");
            log.Say($"agent:        {agent.Subject}");
            log.Say(string.Empty);

            if (!options.Yes && !Confirm())
            {
                log.Problem("Przerwano przed dotknieciem karty.");
                return 2;
            }

            using var card = CardScope.Open(options, log);
            if (card is null)
            {
                return 3;
            }

            IPinPrompt pin = options.PinFromStdin ? new StdinPinPrompt() : new ConsolePinPrompt();
            var outcome = await new IssuanceRunner(client).RunAsync(
                card.Session, target, profile, agent, pin,
                Environment.MachineName, new Progress<string>(log.Key), CancellationToken.None);

            return Report(outcome, log);
        }
        catch (Exception e) when (e is ServerException or IssuanceFailedException
                                      or PersonalisationRefusedException or CertEnrollException)
        {
            log.Say(string.Empty);
            log.Problem(Explain(e), e);
            return 4;
        }
    }

    private static int Report(IssuanceOutcome outcome, Transcript log)
    {
        log.Say(string.Empty);
        log.Say($"wydanie:      {outcome.IssuanceId}");
        log.Say($"karta:        {outcome.Serial}");

        if (outcome.CaRequestId is { } requestId)
        {
            log.Say($"zadanie w CA: {requestId}");
        }

        if (outcome.PendingAtCa)
        {
            log.Say("CA zostawilo zadanie do zatwierdzenia przez menedzera. Karta ma klucz i czeka; "
                    + "wznowienie po zatwierdzeniu to 0022.");
            return 7;
        }

        if (outcome.Certificate is { } certificate)
        {
            log.Say($"certyfikat:   {certificate.Subject}");
            log.Say($"              serial {certificate.SerialNumber}, do {certificate.NotAfter:yyyy-MM-dd}");
            log.Say($"              odcisk {certificate.Thumbprint}");
            log.Say(string.Empty);
            log.Say("Gotowe. Certyfikat jest na karcie i odczytany z niej z powrotem.");
            return 0;
        }

        log.Problem(outcome.Problem ?? "Wydanie nie doszlo do konca.");
        return 4;
    }

    /// <summary>
    /// The enrolment agent certificate, checked before the card is touched.
    /// </summary>
    /// <remarks>
    /// First, and before anything is written: personalising a token that could
    /// never be enrolled wastes the token and the person's time (docs/02,
    /// krok 1).
    /// </remarks>
    private static X509Certificate2? Agent(Options options, Transcript log)
    {
        var agents = EnrolmentAgent.Find();

        foreach (var candidate in agents)
        {
            log.Detail("EA {Thumbprint}: {Subject}, klucz {Key}, wazny {Current}",
                candidate.Thumbprint, candidate.Subject, candidate.HasPrivateKey, candidate.IsCurrent);
        }

        var chosen = options.Agent is { } wanted
            ? agents.FirstOrDefault(c => c.Thumbprint.Equals(wanted, StringComparison.OrdinalIgnoreCase))
            : agents.FirstOrDefault(c => c.IsUsable);

        if (chosen is null)
        {
            log.Problem(Strings.Current[ErrorCodes.AgentMissing]);
            log.Problem("Zapisz sie na szablon Enrollment Agent (EKU 1.3.6.1.4.1.311.20.2.1) i sprobuj ponownie.");
            return null;
        }

        return chosen.Certificate;
    }

    private static async Task<IssuanceProfile?> ChooseProfileAsync(
        ServerClient client, Options options, Transcript log)
    {
        var profiles = await client.ProfilesAsync();

        if (profiles.Count == 0)
        {
            log.Problem("Serwer nie ma zadnego profilu - nie ma czego wydac.");
            return null;
        }

        if (options.ProfileName is { } wanted)
        {
            var named = profiles.FirstOrDefault(p => p.Name.Equals(wanted, StringComparison.OrdinalIgnoreCase));
            if (named is null)
            {
                log.Problem($"Serwer nie ma profilu \"{wanted}\". Ma: {string.Join(", ", profiles.Select(p => p.Name))}");
            }

            return named;
        }

        // One profile is the normal case, and then nobody is asked anything.
        if (profiles.Count == 1)
        {
            return profiles[0];
        }

        log.Say("Profile:");
        for (var i = 0; i < profiles.Count; i++)
        {
            log.Say($"  {i + 1}. {profiles[i].Name}  ({profiles[i].Template})");
        }

        return Pick(profiles, "Numer profilu: ");
    }

    private static async Task<DirectoryUser?> ChooseTargetAsync(ServerClient client, string query, Transcript log)
    {
        var found = await client.SearchAsync(query);

        switch (found.Count)
        {
            case 0:
                log.Problem($"Nikt nie pasuje do \"{query}\".");
                return null;

            case 1:
                return found[0];

            default:
                log.Say($"Pasuje {found.Count} osob:");
                for (var i = 0; i < found.Count; i++)
                {
                    log.Say($"  {i + 1}. {found[i].DisplayName}  {found[i].SamAccount}  {found[i].Upn}");
                }

                return Pick(found, "Numer osoby: ");
        }
    }

    private static T? Pick<T>(IReadOnlyList<T> items, string label) where T : class
    {
        Console.Write(label);
        var typed = Console.ReadLine();

        return int.TryParse(typed, out var index) && index >= 1 && index <= items.Count
            ? items[index - 1]
            : null;
    }

    /// <summary>
    /// The last question before the token stops being factory.
    /// </summary>
    /// <remarks>
    /// Asked even though <c>--yes</c> exists, because everything above this
    /// line is still undoable and nothing below it is.
    /// </remarks>
    private static bool Confirm()
    {
        Console.Write("Zapisac na te karte? Tego nie da sie cofnac [t/N]: ");
        var answer = Console.ReadLine();

        return answer is not null && answer.Trim().StartsWith('t');
    }

    /// <summary>The message key in the operator's language, with the technical detail after it.</summary>
    private static string Explain(Exception e) => e switch
    {
        ServerException server => $"{Strings.Current[server.MessageKey]}  ({server.Message})",
        IssuanceFailedException failed => $"{Strings.Current[failed.MessageKey]}  ({failed.Message})",
        PersonalisationRefusedException refused => $"{Strings.Current[refused.MessageKey]}  ({refused.Message})",
        CertEnrollException cert => $"{Strings.Current[ErrorCodes.CaCmcFailed]}  ({cert.Message} {cert.HResultText})",
        _ => e.Message,
    };
}

using System.Security.Cryptography;
using BlinkyLite.CardLab;
using BlinkyLite.Contracts;
using BlinkyLite.Issuance;
using BlinkyLite.Piv;
using BlinkyLite.Piv.Pcsc;

// The tool for the test station. Two commands:
//
//   inventory                        reads a token and prints what is on it
//   personalise --subject "CN=..."   writes: management key, PUK, PIN, key 9A
//
// "personalise" changes a token in ways nothing can undo, so it refuses to run
// without --yes and refuses any token that is not in its factory state. Every
// run leaves a report to send back and, when it wrote anything, a second file
// with that token's PUK and management key, which does not travel.
return await Run(args);

static async Task<int> Run(string[] args)
{
    var command = args.FirstOrDefault() ?? "help";
    if (command is "help" or "--help" or "-h" or "/?")
    {
        return Help();
    }

    if (!PcscContext.IsSupported)
    {
        Console.Error.WriteLine("This tool speaks to readers through winscard.dll and is Windows-only.");
        return 2;
    }

    var options = Options.Parse(args);
    options.ApplyLanguage();

    // Said before anything happens, and fatal for the command that writes: a
    // mistyped option here was read as "the card is at fault".
    if (options.Unrecognised.Count > 0)
    {
        Console.Error.WriteLine($"Nie rozpoznaje: {string.Join(", ", options.Unrecognised)}. "
                                + "Uruchom bez argumentow, zeby zobaczyc liste opcji.");

        if (command is "personalise" or "eobo-probe")
        {
            return 2;
        }
    }

    Directory.CreateDirectory(options.OutDirectory);

    return command switch
    {
        "inventory" => Inventory(options),
        "personalise" => await Personalise(options),
        "eobo-probe" => await EoboProbe.RunAsync(options),
        _ => Help(),
    };
}

static int Help()
{
    Console.WriteLine("""
        BlinkyLite CardLab - narzedzie stacji testowej

          inventory                      czyta klucz, nic nie zapisuje
          personalise --yes              personalizuje FABRYCZNY klucz:
            --subject "CN=Jan Kowalski"    podmiot zadania, ktore podpisze karta
            --reader <czesc nazwy>         ktory czytnik, gdy jest ich kilka
            --out <katalog>                gdzie zapisac raport i sekrety
            --lang en|de|sv|pl             jezyk komunikatow dla uzytkownika
            --no-ykman                     nie wolaj ykman piv info do raportu
            --pin-from-stdin               czytaj PIN ze standardowego wejscia
                                           zamiast pytac o niego
          eobo-probe                     Q-01 na stacji w domenie: czy
                                         CertEnroll owinie w CMC zadanie
                                         podpisane na cudzej karcie.
                                         NICZEGO NIE WYSYLA do CA:
            --csr <plik>                   raport z personalizacji albo PEM
            --requester DOMENA\uzytkownik  dla kogo ma byc certyfikat
            --agent <odcisk>               ktory certyfikat EA, gdy jest kilka
            --template <nazwa>             tylko do wypisania w raporcie

        Raport (raport-<serial>.txt) mozna odeslac. Plik sekretow
        (sekrety-<serial>.json) otwiera te karte i zostaje na stacji.
        """);
    return 0;
}

static int Inventory(Options options)
{
    var log = new Transcript();

    using var card = CardScope.Open(options, log);
    if (card is null)
    {
        return 3;
    }

    if (!card.Session.Select())
    {
        log.Problem("Karta nie odpowiada apletowi PIV.");
        return 3;
    }

    var inventory = card.Session.ReadInventory();

    log.Say($"serial:       {inventory.SerialNumber?.ToString() ?? "brak (to nie YubiKey)"}");
    log.Say($"firmware:     {inventory.Firmware}");
    log.Say($"management:   {inventory.ManagementKey?.Algorithm.ToString() ?? "?"} "
            + $"{(inventory.ManagementKey?.IsDefault == true ? "FABRYCZNY" : "ustawiony")}");
    log.Say($"PIN:          {inventory.Pin.State}, prob {inventory.Pin.RemainingRetries}/{inventory.Pin.TotalRetries}");
    log.Say($"PUK:          {inventory.Puk.State}, prob {inventory.Puk.RemainingRetries}/{inventory.Puk.TotalRetries}");
    log.Say($"biometria:    {(inventory.IsBiometric ? "tak" : "nie")}");

    foreach (var slot in inventory.Slots)
    {
        log.Say($"slot {slot.Slot}:     "
                + (slot.IsEmpty
                    ? "pusty"
                    : $"klucz {slot.Metadata?.Algorithm}, certyfikat: {slot.HasCertificate}"));
    }

    var ready = inventory.ManagementKey?.IsDefault == true
                && inventory.Slots.First(s => s.Slot == PivSlot.Authentication).IsEmpty;

    log.Say(string.Empty);
    log.Say(ready
        ? "Ten klucz wyglada na fabryczny - personalizacja powinna przejsc."
        : "Ten klucz nie jest fabryczny - personalizacja go odmowi i nic nie zapisze.");

    return 0;
}

static async Task<int> Personalise(Options options)
{
    var log = new Transcript();

    if (!options.Yes)
    {
        log.Problem("personalise zapisuje nowy management key, PUK i PIN oraz generuje klucz w slocie 9A. "
                    + "Tego nie da sie cofnac. Dodaj --yes (dwa myslniki), gdy klucz jest testowy.");
        return 2;
    }

    // In the product these come from the server, sealed in envelopes before the
    // card is touched (D-03). On the bench they are made here and written to a
    // file, because there is nowhere else to keep them yet.
    var managementKey = RandomNumberGenerator.GetBytes(24);
    var puk = string.Concat(Enumerable.Range(0, 8).Select(_ => RandomNumberGenerator.GetInt32(0, 10)));
    var request = new PersonalisationRequest(managementKey, puk);

    TokenInventory? before = null;
    PersonalisationResult? result = null;
    string? refusal = null;
    string reader;

    // The card is held only for as long as the work takes: the report is
    // written afterwards, and ykman needs the reader back.
    using (var card = CardScope.Open(options, log))
    {
        if (card is null)
        {
            return 3;
        }

        reader = card.Reader;

        if (!card.Session.Select())
        {
            log.Problem("Karta nie odpowiada apletowi PIV.");
            return 3;
        }

        before = card.Session.ReadInventory();

        IPinPrompt prompt = options.PinFromStdin ? new StdinPinPrompt() : new ConsolePinPrompt();
        var progress = new Progress<string>(log.Key);

        try
        {
            result = await new CardPersonaliser().PersonaliseAsync(
                card.Session, request, prompt, options.Subject, progress);
        }
        catch (PersonalisationRefusedException e)
        {
            // The message key is what the client will show; the detail is for
            // whoever reads the report.
            refusal = $"{Strings.Current[e.MessageKey]}  ({e.Message})";
            log.Problem(refusal);
        }
        catch (PivException e)
        {
            refusal = $"{Strings.Current["issuance.step.failed"]} {e.Message}";
            log.Problem(refusal);
        }
    }

    var ykman = options.NoYkman ? null : Report.Ykman();

    var serial = result?.Serial ?? before?.SerialNumber ?? 0;
    var reportPath = await Report.WriteAsync(
        Path.Combine(options.OutDirectory, $"raport-{serial}.txt"),
        log, reader, before, result, refusal, ykman);

    Console.WriteLine();
    Console.WriteLine($"raport:       {reportPath}");
    Console.WriteLine("              Ten plik mozna odeslac - nie ma w nim PIN-u, PUK-u ani management key.");

    if (result is null)
    {
        return 4;
    }

    var secretsPath = await Report.WriteSecretsAsync(
        Path.Combine(options.OutDirectory, $"sekrety-{result.Serial}.json"),
        result.Serial, puk, managementKey, result.ManagementKeyAlgorithm);

    Console.WriteLine($"sekrety:      {secretsPath}");
    Console.WriteLine("              PUK i management key tej karty, jawnie. Nie wysylaj, skasuj po tescie.");
    Console.WriteLine();
    Console.WriteLine(result.Checks.Passed
        ? "Sprawdzenia po zapisie: OK."
        : "Sprawdzenia po zapisie: COS SIE NIE ZGADZA - patrz raport.");

    return result.Checks.Passed ? 0 : 5;
}

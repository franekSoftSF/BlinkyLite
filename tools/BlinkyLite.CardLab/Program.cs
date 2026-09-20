using System.Security.Cryptography;
using BlinkyLite.CardLab;
using BlinkyLite.Contracts;
using BlinkyLite.Issuance;
using BlinkyLite.Piv;
using BlinkyLite.Piv.Pcsc;
using Serilog;

// The tool for the test station. Three commands:
//
//   inventory                        reads a token and prints what is on it
//   personalise --subject "CN=..."   writes: management key, PUK, PIN, key 9A
//   eobo-probe --csr <plik>          asks CertEnroll the question in Q-01
//
// "personalise" changes a token in ways nothing can undo, so it refuses to run
// without --yes and refuses any token that is not in its factory state. Every
// run leaves a report to send back, a log with everything that happened, and -
// when it wrote to a card - a file with that token's PUK and management key,
// which does not travel.
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

    Directory.CreateDirectory(options.OutDirectory);
    var log = Transcript.Start(command, args, options.OutDirectory);

    try
    {
        // Said before anything happens, and fatal for the commands that act: a
        // mistyped option here was once read as "the card is at fault".
        if (options.Unrecognised.Count > 0)
        {
            log.Problem($"Nie rozpoznaje: {string.Join(", ", options.Unrecognised)}. "
                        + "Uruchom bez argumentow, zeby zobaczyc liste opcji.");

            if (command is "personalise" or "eobo-probe")
            {
                return 2;
            }
        }

        return command switch
        {
            "inventory" => Inventory(options, log),
            "personalise" => await Personalise(options, log),
            "eobo-probe" => await EoboProbe.RunAsync(options, log),
            _ => Help(),
        };
    }
    catch (Exception e)
    {
        // Nothing should reach here. If something does, the log is the only
        // place the reason will still exist tomorrow.
        log.Problem($"Nieoczekiwany blad: {e.Message}", e);
        return 9;
    }
    finally
    {
        Console.WriteLine($"log:          {log.LogPath}");
        await Log.CloseAndFlushAsync();
    }
}

static int Help()
{
    Console.WriteLine("""
        BlinkyLite CardLab - narzedzie stacji testowej

          inventory                      czyta klucz, nic nie zapisuje
          personalise --yes              personalizuje FABRYCZNY klucz:
            --subject "CN=Jan Kowalski"    podmiot zadania, ktore podpisze karta
            --reader <czesc nazwy>         ktory czytnik, gdy jest ich kilka
            --out <katalog>                gdzie zapisac raport, log i sekrety
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

        Kazdy przebieg zostawia log (cardlab-<data>-<komenda>.log) - to jego
        odsylaj, gdy cos nie wyjdzie. Raport (raport-<serial>.txt) tez mozna
        odeslac. Plik sekretow (sekrety-<serial>.json) otwiera te karte i
        zostaje na stacji.
        """);
    return 0;
}

static int Inventory(Options options, Transcript log)
{
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
    Describe(inventory, log);

    var ready = inventory.ManagementKey?.IsDefault == true
                && inventory.Slots.First(s => s.Slot == PivSlot.Authentication).IsEmpty;

    log.Say(string.Empty);
    log.Say(ready
        ? "Ten klucz wyglada na fabryczny - personalizacja powinna przejsc."
        : "Ten klucz nie jest fabryczny - personalizacja go odmowi i nic nie zapisze.");

    return 0;
}

static void Describe(TokenInventory inventory, Transcript log)
{
    log.Say($"serial:       {inventory.SerialNumber?.ToString() ?? "brak (to nie YubiKey)"}");
    log.Say($"firmware:     {inventory.Firmware}");
    log.Say($"management:   {inventory.ManagementKey?.Algorithm.ToString() ?? "?"} "
            + $"{(inventory.ManagementKey?.IsDefault == true ? "FABRYCZNY" : "ustawiony")}");
    log.Say($"PIN:          {inventory.Pin.State}, prob {inventory.Pin.RemainingRetries}/{inventory.Pin.TotalRetries}");
    log.Say($"PUK:          {inventory.Puk.State}, prob {inventory.Puk.RemainingRetries}/{inventory.Puk.TotalRetries}");
    log.Say($"biometria:    {(inventory.IsBiometric ? "tak" : "nie")}");

    log.Detail("management key: dotyk {Touch}", inventory.ManagementKey?.TouchPolicy);

    foreach (var slot in inventory.Slots)
    {
        log.Say($"slot {slot.Slot}:     "
                + (slot.IsEmpty
                    ? "pusty"
                    : $"klucz {slot.Metadata?.Algorithm}, certyfikat: {slot.HasCertificate}"));

        log.Detail("slot {Slot}: metadane {Metadata}, certyfikat {Bytes} bajtow",
            slot.Slot, slot.Metadata, slot.CertificateDer?.Length ?? 0);
    }
}

static async Task<int> Personalise(Options options, Transcript log)
{
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

    log.Detail("zadanie: klucz {Algorithm}, polityka PIN {PinPolicy}, dotyk {TouchPolicy}, podmiot {Subject}",
        request.KeyAlgorithm, request.PinPolicy, request.TouchPolicy, options.Subject);

    TokenInventory? before;
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
        Describe(before, log);
        log.Say(string.Empty);

        IPinPrompt prompt = options.PinFromStdin ? new StdinPinPrompt() : new ConsolePinPrompt();
        var progress = new Progress<string>(log.Key);

        try
        {
            result = await new CardPersonaliser().PersonaliseAsync(
                card.Session, request, prompt, options.Subject, progress);

            log.Detail("sprawdzenia po zapisie: {Checks}", result.Checks);
            log.Detail("atestacja: firmware {Firmware}, serial {Serial}, obudowa {Form}, FIPS {Fips}",
                result.Attestation.Firmware, result.Attestation.SerialNumber,
                result.Attestation.FormFactor, result.Attestation.IsFipsDevice);
        }
        catch (PersonalisationRefusedException e)
        {
            // The message key is what the client will show; the detail is for
            // whoever reads the report.
            refusal = $"{Strings.Current[e.MessageKey]}  ({e.Message})";
            log.Problem(refusal, e);
        }
        catch (PivException e)
        {
            refusal = $"{Strings.Current["issuance.step.failed"]} {e.Message}";
            log.Problem(refusal, e);
        }
    }

    var ykman = options.NoYkman ? null : Report.Ykman();
    log.Detail("ykman piv info: {Result}", ykman is null ? "nie ma go na tej stacji" : $"{ykman.Length} znakow");

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

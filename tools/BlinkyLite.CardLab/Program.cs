using System.Security.Cryptography;
using System.Text.Json;
using BlinkyLite.CardLab;
using BlinkyLite.Contracts;
using BlinkyLite.Issuance;
using BlinkyLite.Piv;
using BlinkyLite.Piv.Pcsc;

// The bench tool for the issuance engine. Two commands:
//
//   inventory                        reads a token and prints what is on it
//   personalise --subject "CN=..."   writes: management key, PUK, PIN, key 9A
//
// "personalise" changes a token in ways nothing can undo, so it refuses to run
// without --yes and refuses any token that is not in its factory state.
return await Run(args);

static async Task<int> Run(string[] args)
{
    if (!PcscContext.IsSupported)
    {
        Console.Error.WriteLine("This tool speaks to readers through winscard.dll and is Windows-only.");
        return 2;
    }

    var command = args.FirstOrDefault() ?? "help";
    var options = Options.Parse(args);

    using var context = PcscContext.Establish();
    var readers = context.ListReaders();
    if (readers.Count == 0)
    {
        Console.Error.WriteLine("No readers. Is the token plugged in?");
        return 3;
    }

    var reader = options.Reader is { } wanted
        ? readers.FirstOrDefault(r => r.Contains(wanted, StringComparison.OrdinalIgnoreCase))
        : readers.FirstOrDefault(r => r.Contains("yubikey", StringComparison.OrdinalIgnoreCase));

    if (reader is null)
    {
        Console.Error.WriteLine($"No matching reader. Readers: {string.Join(", ", readers)}");
        return 3;
    }

    Console.WriteLine($"reader: {reader}");

    using var transport = context.Connect(reader);
    if (transport is null)
    {
        Console.Error.WriteLine("The reader has no card.");
        return 3;
    }

    using var connection = new PivConnection(transport);
    var session = new PivSession(connection);

    return command switch
    {
        "inventory" => Inventory(session),
        "personalise" => await Personalise(session, options),
        _ => Help(),
    };
}

static int Help()
{
    Console.WriteLine("""
        BlinkyLite.CardLab

          inventory                      read a token, write nothing
          personalise --yes              personalise a FACTORY token:
            --subject "CN=Jan Kowalski"    subject of the request the card signs
            --reader <part of name>        which reader, if there are several
            --out <file>                   where to save PUK, management key,
                                           attestation and request (lab only)
            --pin-from-stdin               read the PIN from standard input
                                           instead of asking for it
        """);
    return 0;
}

static int Inventory(PivSession session)
{
    if (!session.Select())
    {
        Console.Error.WriteLine("The card does not answer to the PIV applet.");
        return 3;
    }

    var inventory = session.ReadInventory();

    Console.WriteLine($"serial:        {inventory.SerialNumber?.ToString() ?? "none (not a YubiKey)"}");
    Console.WriteLine($"firmware:      {inventory.Firmware}");
    Console.WriteLine($"management:    {inventory.ManagementKey?.Algorithm} "
                      + $"{(inventory.ManagementKey?.IsDefault == true ? "FACTORY" : "set")}");
    Console.WriteLine($"PIN:           {inventory.Pin.State}, {inventory.Pin.RemainingRetries}/{inventory.Pin.TotalRetries} left");
    Console.WriteLine($"PUK:           {inventory.Puk.State}, {inventory.Puk.RemainingRetries}/{inventory.Puk.TotalRetries} left");
    Console.WriteLine($"biometrics:    {(inventory.IsBiometric ? "yes" : "no")}");

    foreach (var slot in inventory.Slots)
    {
        Console.WriteLine($"slot {slot.Slot}:       "
                          + (slot.IsEmpty ? "empty" : $"key {slot.Metadata?.Algorithm}, certificate: {slot.HasCertificate}"));
    }

    return 0;
}

static async Task<int> Personalise(PivSession session, Options options)
{
    if (!options.Yes)
    {
        Console.Error.WriteLine(
            "personalise writes a new management key, PUK and PIN and generates a key in 9A. "
            + "Nothing here can be undone. Pass --yes when the token is a test one.");
        return 2;
    }

    // In the product these come from the server, sealed in envelopes before the
    // card is touched (D-03). On the bench they are made here and written to a
    // file, because there is nowhere else to keep them yet.
    var managementKey = RandomNumberGenerator.GetBytes(24);
    var puk = string.Concat(Enumerable.Range(0, 8).Select(_ => RandomNumberGenerator.GetInt32(0, 10)));

    var request = new PersonalisationRequest(managementKey, puk);
    IPinPrompt prompt = options.PinFromStdin ? new StdinPinPrompt() : new ConsolePinPrompt();
    var progress = new Progress<string>(step => Console.WriteLine($"  {step}"));

    try
    {
        var result = await new CardPersonaliser().PersonaliseAsync(
            session, request, prompt, options.Subject, progress);

        Console.WriteLine();
        Console.WriteLine($"serial:        {result.Serial}");
        Console.WriteLine($"firmware:      {result.Firmware}");
        Console.WriteLine($"management:    {result.ManagementKeyAlgorithm} (also in PRINTED, behind the PIN)");
        Console.WriteLine($"wrote CHUID:   {result.WroteChuid}   CCC: {result.WroteCcc}");
        Console.WriteLine($"attestation:   firmware {result.Attestation.Firmware}, "
                          + $"form factor {result.Attestation.FormFactor}, "
                          + $"PIN policy {result.Attestation.PinPolicy}, touch {result.Attestation.TouchPolicy}");
        Console.WriteLine($"request:       {result.CsrDer.Length} bytes, signed by the card");

        var file = options.Out ?? $"cardlab-{result.Serial}.json";
        await File.WriteAllTextAsync(file, JsonSerializer.Serialize(new
        {
            serial = result.Serial,
            firmware = result.Firmware.ToString(),
            puk,
            managementKey = Convert.ToBase64String(managementKey),
            managementKeyAlgorithm = result.ManagementKeyAlgorithm.ToString(),
            attestation = Convert.ToBase64String(result.AttestationDer),
            intermediate = Convert.ToBase64String(result.IntermediateDer),
            csr = Convert.ToBase64String(result.CsrDer),
        }, new JsonSerializerOptions { WriteIndented = true }));

        Console.WriteLine($"saved:         {file}");
        Console.WriteLine("That file holds the PUK and the management key of this token in the clear.");
        Console.WriteLine("It exists because there is no server in this patch yet - keep it out of the repository.");
        return 0;
    }
    catch (PersonalisationRefusedException e)
    {
        Console.Error.WriteLine($"refused: {e.Message}");
        return 4;
    }
}

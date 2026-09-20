using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using BlinkyLite.Issuance;
using BlinkyLite.Piv;

namespace BlinkyLite.CardLab;

/// <summary>
/// The two files a bench run leaves behind, and why they are two.
/// </summary>
/// <remarks>
/// The report is meant to be sent: it holds what the card did, and nothing
/// that would let anyone use the card. The secrets file holds the PUK and the
/// management key of that one token, and is the file that must not travel.
/// One file with a warning at the top would be sent anyway - the split is the
/// only thing that survives a hurried day.
/// </remarks>
internal static class Report
{
    /// <summary>The report, safe to send.</summary>
    public static async Task<string> WriteAsync(
        string path,
        Transcript transcript,
        string reader,
        TokenInventory? before,
        PersonalisationResult? result,
        string? refusal,
        string? ykman)
    {
        var text = new StringBuilder();

        text.AppendLine("BlinkyLite CardLab - raport z personalizacji");
        text.AppendLine(new string('=', 60));
        text.AppendLine($"czas (UTC):   {DateTimeOffset.UtcNow:yyyy-MM-dd HH:mm:ss}");
        text.AppendLine($"stacja:       {Environment.MachineName}");
        text.AppendLine($"system:       {Environment.OSVersion.VersionString}");
        text.AppendLine($"narzedzie:    {typeof(Report).Assembly.GetName().Version}");
        text.AppendLine($"czytnik:      {reader}");
        text.AppendLine();

        if (before is not null)
        {
            text.AppendLine("KARTA PRZED");
            text.AppendLine(Describe(before));
        }

        text.AppendLine("PRZEBIEG");
        text.AppendLine(transcript.ToString().TrimEnd());
        text.AppendLine();

        if (refusal is not null)
        {
            text.AppendLine("WYNIK: ODMOWA");
            text.AppendLine($"  {refusal}");
            text.AppendLine("  Nic nie zostalo zapisane na karcie.");
        }

        if (result is not null)
        {
            var checks = result.Checks;

            text.AppendLine($"WYNIK: {(checks.Passed ? "OK" : "SPRAWDZENIA NIE PRZESZLY")}");
            text.AppendLine($"  serial:                 {result.Serial}");
            text.AppendLine($"  firmware:               {result.Firmware}");
            text.AppendLine($"  management key:         {result.ManagementKeyAlgorithm}");
            text.AppendLine($"  CHUID zapisany:         {Yes(result.WroteChuid)}");
            text.AppendLine($"  CCC zapisany:           {Yes(result.WroteCcc)}");
            text.AppendLine();
            text.AppendLine("SPRAWDZENIA PO ZAPISIE (odczytane z karty, nie zalozone)");
            text.AppendLine($"  {Mark(checks.ManagementKeyReadsBack)} management key wraca z PRINTED taki sam, jaki zapisalismy");
            text.AppendLine($"  {Mark(checks.ManagementKeyBehindPin)} ADMIN DATA mowi, ze management key stoi za PIN-em");
            text.AppendLine($"  {Mark(checks.RequestSignatureValid)} zadanie (CSR) ma poprawny podpis karty");
            text.AppendLine($"  {Mark(checks.Pin == PinState.Set)} PIN: {checks.Pin} (fabryczny bylby Default)");
            text.AppendLine($"  {Mark(checks.Puk == PinState.Set)} PUK: {checks.Puk} (fabryczny bylby Default)");
            text.AppendLine($"  {Mark(checks.SlotOrigin == KeyOrigin.Generated)} slot 9A: klucz {checks.SlotAlgorithm}, pochodzenie {checks.SlotOrigin}");
            text.AppendLine($"       polityka PIN {checks.SlotPinPolicy}, dotyk {checks.SlotTouchPolicy}");
            text.AppendLine();
            text.AppendLine("ATESTACJA YUBICO (zweryfikowana do zakotwiczonego roota)");
            text.AppendLine($"  firmware z atestacji:   {result.Attestation.Firmware}");
            text.AppendLine($"  serial z atestacji:     {result.Attestation.SerialNumber}");
            text.AppendLine($"  obudowa:                {result.Attestation.FormFactor}");
            text.AppendLine($"  polityka PIN / dotyk:   {result.Attestation.PinPolicy} / {result.Attestation.TouchPolicy}");
            text.AppendLine($"  FIPS:                   {Yes(result.Attestation.IsFipsDevice)}");
            text.AppendLine();
        }

        if (ykman is not null)
        {
            text.AppendLine("ykman piv info");
            text.AppendLine(ykman.TrimEnd());
            text.AppendLine();
        }

        if (result is not null)
        {
            // In PEM, because these are the things somebody else may want to
            // check by hand, and every tool that checks them reads PEM.
            text.AppendLine(PemEncoding.WriteString("CERTIFICATE REQUEST", result.CsrDer));
            text.AppendLine();
            text.AppendLine("# atestacja slotu 9A");
            text.AppendLine(PemEncoding.WriteString("CERTIFICATE", result.AttestationDer));
            text.AppendLine();
            text.AppendLine("# certyfikat atestacyjny karty (F9)");
            text.AppendLine(PemEncoding.WriteString("CERTIFICATE", result.IntermediateDer));
        }

        await File.WriteAllTextAsync(path, text.ToString(), new UTF8Encoding(false));
        return path;
    }

    /// <summary>The PUK and the management key of one token. This file does not travel.</summary>
    public static async Task<string> WriteSecretsAsync(
        string path, uint serial, string puk, byte[] managementKey, PivAlgorithm algorithm)
    {
        var json = JsonSerializer.Serialize(new
        {
            ostrzezenie = "Ten plik otwiera te karte. Nie wysylaj go, nie wkladaj do repozytorium, "
                          + "skasuj po tescie. W produkcie te wartosci powstaja na serwerze i nigdy "
                          + "nie leza jawnie (D-03).",
            serial,
            puk,
            managementKey = Convert.ToBase64String(managementKey),
            managementKeyAlgorithm = algorithm.ToString(),
            utworzono = DateTimeOffset.UtcNow,
        }, new JsonSerializerOptions { WriteIndented = true });

        await File.WriteAllTextAsync(path, json, new UTF8Encoding(false));
        return path;
    }

    /// <summary>
    /// <c>ykman piv info</c>, when the station has it. A second opinion from
    /// Yubico's own tool is worth more in a report than ours repeated twice.
    /// </summary>
    /// <remarks>
    /// Runs only after the PC/SC transaction is over: ykman opens the same
    /// reader, and two owners of one reader is SCARD_E_SHARING_VIOLATION.
    /// </remarks>
    public static string? Ykman()
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo("ykman", "piv info")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            });

            if (process is null)
            {
                return null;
            }

            var output = process.StandardOutput.ReadToEnd() + process.StandardError.ReadToEnd();
            return process.WaitForExit(30_000) ? output : null;
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            // Not installed, which is the normal case on a fresh station.
            return null;
        }
    }

    private static string Describe(TokenInventory inventory)
    {
        var text = new StringBuilder();

        text.AppendLine($"  serial:       {inventory.SerialNumber?.ToString() ?? "brak (to nie YubiKey)"}");
        text.AppendLine($"  firmware:     {inventory.Firmware}");
        text.AppendLine($"  management:   {inventory.ManagementKey?.Algorithm.ToString() ?? "?"} "
                        + $"{(inventory.ManagementKey?.IsDefault == true ? "FABRYCZNY" : "ustawiony")}");
        text.AppendLine($"  PIN:          {inventory.Pin.State}, prob {inventory.Pin.RemainingRetries}/{inventory.Pin.TotalRetries}");
        text.AppendLine($"  PUK:          {inventory.Puk.State}, prob {inventory.Puk.RemainingRetries}/{inventory.Puk.TotalRetries}");
        text.AppendLine($"  biometria:    {Yes(inventory.IsBiometric)}");

        foreach (var slot in inventory.Slots)
        {
            text.AppendLine($"  slot {slot.Slot}:     "
                            + (slot.IsEmpty
                                ? "pusty"
                                : $"klucz {slot.Metadata?.Algorithm}, certyfikat: {Yes(slot.HasCertificate)}"));
        }

        return text.ToString();
    }

    private static string Yes(bool value) => value ? "tak" : "nie";

    private static string Mark(bool value) => value ? "[ok]" : "[!!]";
}

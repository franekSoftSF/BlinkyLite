using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using BlinkyLite.Issuance.Eobo;

namespace BlinkyLite.CardLab;

/// <summary>
/// Answers Q-01 on a station in the domain: does CertEnroll wrap a PKCS#10
/// that was signed on somebody's card, by a key this machine does not have?
/// </summary>
/// <remarks>
/// Builds the CMC and stops there. Submitting it would make the CA issue a
/// real certificate to a real person, which is not a thing a probe does - and
/// the question is answered before the submission anyway: if CertEnroll
/// refuses the inner request, 0021 is written around the hand-made CMC from
/// the sibling project instead.
/// </remarks>
internal static class EoboProbe
{
    public static async Task<int> RunAsync(Options options)
    {
        var log = new Transcript();

        if (options.Csr is not { } csrPath)
        {
            log.Problem("Podaj --csr <plik>: raport z personalizacji albo zadanie w PEM.");
            return 2;
        }

        if (options.Requester is not { } requester)
        {
            log.Problem("Podaj --requester DOMENA\\uzytkownik - to imie, na ktore CA wystawi certyfikat.");
            return 2;
        }

        byte[] pkcs10;
        try
        {
            pkcs10 = ReadRequest(await File.ReadAllTextAsync(csrPath));
        }
        catch (Exception e) when (e is IOException or FormatException or CryptographicException)
        {
            log.Problem($"Nie moge odczytac zadania z {csrPath}: {e.Message}");
            return 2;
        }

        log.Say($"zadanie:      {pkcs10.Length} bajtow z {Path.GetFileName(csrPath)}");
        log.Say($"podmiot:      {Subject(pkcs10)}");
        log.Say($"requester:    {requester}");
        log.Say(string.Empty);

        var agents = EnrolmentAgent.Find();
        log.Say($"certyfikaty Enrollment Agenta w CurrentUser\\My: {agents.Count}");

        foreach (var candidate in agents)
        {
            log.Say($"  {candidate.Thumbprint}  {candidate.Subject}");
            log.Say($"      klucz prywatny: {(candidate.HasPrivateKey ? "jest" : "BRAK")}, "
                    + $"waznosc: {(candidate.IsCurrent ? "aktualny" : "POZA ZAKRESEM")}, "
                    + $"do {candidate.Certificate.NotAfter:yyyy-MM-dd}");
        }

        var agent = options.Agent is { } wanted
            ? agents.FirstOrDefault(c => c.Thumbprint.Equals(wanted, StringComparison.OrdinalIgnoreCase))
            : agents.FirstOrDefault(c => c.IsUsable);

        if (agent is null)
        {
            log.Problem(options.Agent is not null
                ? "Nie ma certyfikatu EA o takim odcisku."
                : "Nie ma uzywalnego certyfikatu Enrollment Agenta. Zapisz sie na szablon Enrollment Agent "
                  + "(EKU 1.3.6.1.4.1.311.20.2.1) i uruchom ponownie.");
            return 3;
        }

        log.Say(string.Empty);
        log.Say($"podpisuje:    {agent.Subject}");
        log.Say($"              {agent.Thumbprint}");
        log.Say(string.Empty);

        var attempt = CertEnrollCmc.Build(pkcs10, requester, agent.Certificate);

        log.Say("CertEnroll, krok po kroku:");
        log.Say($"  {Mark(attempt.InnerRequestAccepted)} InitializeDecode + InitializeFromInnerRequest "
                + "(zadanie podpisane kluczem, ktorego ta maszyna nie ma)");
        log.Say($"  {Mark(attempt.RequesterNameAccepted)} RequesterName = {requester}");
        log.Say($"  {Mark(attempt.SignerAccepted)} SignerCertificate = certyfikat EA");
        log.Say($"  {Mark(attempt.Encoded)} Encode");

        if (attempt.Failure is { } failure)
        {
            log.Say(string.Empty);
            log.Problem($"ODMOWA na kroku \"{failure.Step}\", HRESULT {failure.HResultText}");
            log.Problem(failure.Message);
        }

        log.Say(string.Empty);
        log.Say(attempt.Succeeded
            ? $"Q-01: TAK. CertEnroll przyjal zadanie z karty; CMC ma {attempt.Cmc!.Length} znakow base64."
            : "Q-01: NIE na tej stacji. Patrz krok, ktory nie przeszedl.");

        if (options.Template is { } template)
        {
            log.Say($"Do ICertRequest3.Submit poszloby: CertificateTemplate:{template} (ten probnik nic nie wysyla).");
        }

        var report = Path.Combine(options.OutDirectory, "q01-certenroll.txt");
        await File.WriteAllTextAsync(report, Header() + log, new UTF8Encoding(false));
        Console.WriteLine();
        Console.WriteLine($"raport:       {report}");

        if (attempt.Succeeded)
        {
            // The CMC is the cardholder's public request signed by the
            // operator: nothing secret, and the only artefact worth keeping to
            // show somebody what CertEnroll actually produced.
            var cmc = Path.Combine(options.OutDirectory, "q01-cmc.b64");
            await File.WriteAllTextAsync(cmc, attempt.Cmc!, new UTF8Encoding(false));
            Console.WriteLine($"CMC:          {cmc}");
        }

        return attempt.Succeeded ? 0 : 6;
    }

    private static string Header() =>
        $"""
        BlinkyLite CardLab - sonda Q-01 (CertEnroll CMC)
        ============================================================
        czas (UTC):   {DateTimeOffset.UtcNow:yyyy-MM-dd HH:mm:ss}
        stacja:       {Environment.MachineName}
        uzytkownik:   {Environment.UserDomainName}\{Environment.UserName}
        system:       {Environment.OSVersion.VersionString}

        """;

    /// <summary>
    /// The request out of a PEM block, whether it stands alone or sits in a
    /// report among everything else that run printed.
    /// </summary>
    private static byte[] ReadRequest(string text)
    {
        var fields = PemEncoding.Find(text);
        var label = text[fields.Label];

        return label == "CERTIFICATE REQUEST"
            ? Convert.FromBase64String(text[fields.Base64Data].Replace("\r", string.Empty).Replace("\n", string.Empty))
            : throw new FormatException($"the first PEM block is {label}, not CERTIFICATE REQUEST");
    }

    private static string Subject(byte[] pkcs10)
    {
        try
        {
            return CertificateRequest.LoadSigningRequest(pkcs10, HashAlgorithmName.SHA256).SubjectName.Name;
        }
        catch (CryptographicException e)
        {
            return $"nie da sie odczytac ({e.Message})";
        }
    }

    private static string Mark(bool value) => value ? "[ok]" : "[!!]";
}

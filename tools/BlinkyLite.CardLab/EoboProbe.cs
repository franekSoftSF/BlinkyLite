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
/// Builds the CMC, reads it back and stops there. Submitting it would make the
/// CA issue a real certificate to a real person, which is not a thing a probe
/// does - and the question is answered before the submission anyway: if
/// CertEnroll refuses the inner request, 0021 is written around the hand-made
/// CMC from the sibling project instead.
/// </remarks>
internal static class EoboProbe
{
    /// <summary>
    /// Reads a CMC somebody already has and says what is inside it.
    /// </summary>
    /// <remarks>
    /// Needs no station, no card and no agent certificate, which is the point:
    /// the first probe run reported a requester name that looked wrong, and
    /// answering "is it the CMC or the reading?" should not cost a trip to a
    /// machine in the domain.
    /// </remarks>
    public static async Task<int> InspectAsync(Options options, Transcript log)
    {
        if (options.Cmc is not { } path)
        {
            log.Problem("Podaj --cmc <plik>: CMC w base64, taki jak q01-cmc.b64.");
            return 2;
        }

        byte[] der;
        try
        {
            der = Convert.FromBase64String((await File.ReadAllTextAsync(path)).Trim());
        }
        catch (Exception e) when (e is IOException or FormatException)
        {
            log.Problem($"Nie moge odczytac CMC z {path}: {e.Message}", e);
            return 2;
        }

        log.Say($"CMC:          {der.Length} bajtow z {Path.GetFileName(path)}");

        var wanted = options.Requester;
        var ok = ReadBack(Convert.ToBase64String(der), wanted, log);

        return ok || wanted is null ? 0 : 6;
    }

    public static async Task<int> RunAsync(Options options, Transcript log)
    {
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
            log.Problem($"Nie moge odczytac zadania z {csrPath}: {e.Message}", e);
            return 2;
        }

        log.Say($"zadanie:      {pkcs10.Length} bajtow z {Path.GetFileName(csrPath)}");
        DescribeRequest(pkcs10, log);
        log.Say($"requester:    {requester}");
        log.Say(string.Empty);

        var agent = ChooseAgent(options, log);
        if (agent is null)
        {
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
            log.Problem($"ODMOWA na kroku \"{failure.Step}\", HRESULT {failure.HResultText}", failure);
            log.Problem(failure.Message);
            log.Detail("HRESULT {HResult} ({Known})", failure.HResultText, Known(failure.HResult));
        }

        var inside = attempt.Succeeded ? ReadBack(attempt.Cmc!, requester, log) : true;

        log.Say(string.Empty);
        log.Say(attempt.Succeeded && inside
            ? "Q-01: TAK. CertEnroll przyjal zadanie z karty, a w CMC jest to, co CA musi zobaczyc."
            : attempt.Succeeded
                ? "Q-01: CZESCIOWO. CMC powstal, ale jego tresc sie nie zgadza - patrz wyzej."
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

        return attempt.Succeeded && inside ? 0 : 6;
    }

    /// <summary>
    /// Reads the CMC back and says whether it carries what the CA needs.
    /// </summary>
    /// <remarks>
    /// "Encode did not throw" is not the answer to Q-01. The requester name is
    /// what decides whether the certificate comes out in the cardholder's name
    /// or in the operator's, so the probe checks that the name it asked for is
    /// really in there.
    /// </remarks>
    private static bool ReadBack(string base64, string? requester, Transcript log)
    {
        var contents = CmcInspection.Inspect(Convert.FromBase64String(base64));

        log.Say(string.Empty);
        log.Say("Co naprawde jest w CMC:");
        log.Say($"  {Mark(contents.IsPkiData)} tresc: {contents.ContentType}"
                + (contents.IsPkiData ? " (PKIData)" : " - a mial byc PKIData"));

        // Compared without case, because a domain does not distinguish it, and
        // after decoding: CertEnroll percent-encodes the backslash.
        var nameMatches = requester is null
            ? contents.RequesterName is not null
            : string.Equals(contents.RequesterName, requester, StringComparison.OrdinalIgnoreCase);

        log.Say($"  {Mark(nameMatches)} requestername = {contents.RequesterName ?? "BRAK"}"
                + (requester is null || nameMatches ? string.Empty : $"  (prosilismy o {requester})"));

        log.Say($"  {Mark(contents.HasEnoughSigners)} podpisow: {contents.Signers.Count} "
                + "(MS-WCCE chce co najmniej dwoch: zgloszeniodawcy i agenta)");

        foreach (var signer in contents.Signers)
        {
            log.Say($"      {signer.DigestAlgorithm}  {signer.Subject}");
        }

        log.Detail("kontrole w PKIData: {Controls}", string.Join(", ", contents.Controls));

        if (contents.Problem is { } problem)
        {
            log.Problem($"CMC: {problem}");
        }

        if (!nameMatches)
        {
            log.Problem("Bez requestername CA zbuduje podmiot dla tego, kto wolal, czyli dla operatora.");
        }

        return contents.IsPkiData && nameMatches && contents.Problem is null;
    }

    private static EnrolmentAgent.Candidate? ChooseAgent(Options options, Transcript log)
    {
        var agents = EnrolmentAgent.Find();
        log.Say($"certyfikaty Enrollment Agenta w CurrentUser\\My: {agents.Count}");

        foreach (var candidate in agents)
        {
            log.Say($"  {candidate.Thumbprint}  {candidate.Subject}");
            log.Say($"      klucz prywatny: {(candidate.HasPrivateKey ? "jest" : "BRAK")}, "
                    + $"waznosc: {(candidate.IsCurrent ? "aktualny" : "POZA ZAKRESEM")}, "
                    + $"do {candidate.Certificate.NotAfter:yyyy-MM-dd}");

            log.Detail("EA {Thumbprint}: wystawca {Issuer}, od {From} do {To}, klucz {Algorithm}, szablon {Template}",
                candidate.Thumbprint, candidate.Certificate.Issuer,
                candidate.Certificate.NotBefore, candidate.Certificate.NotAfter,
                candidate.Certificate.PublicKey.Oid.FriendlyName, TemplateOf(candidate.Certificate));
        }

        var chosen = options.Agent is { } wanted
            ? agents.FirstOrDefault(c => c.Thumbprint.Equals(wanted, StringComparison.OrdinalIgnoreCase))
            : agents.FirstOrDefault(c => c.IsUsable);

        if (chosen is null)
        {
            log.Problem(options.Agent is not null
                ? "Nie ma certyfikatu EA o takim odcisku."
                : "Nie ma uzywalnego certyfikatu Enrollment Agenta. Zapisz sie na szablon Enrollment Agent "
                  + "(EKU 1.3.6.1.4.1.311.20.2.1) i uruchom ponownie.");
        }

        return chosen;
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

    private static void DescribeRequest(byte[] pkcs10, Transcript log)
    {
        try
        {
            var request = CertificateRequest.LoadSigningRequest(pkcs10, HashAlgorithmName.SHA256);

            log.Say($"podmiot:      {request.SubjectName.Name}");
            log.Detail("zadanie: klucz {Algorithm} {Size} bitow, podpis sprawdzony przy wczytaniu",
                request.PublicKey.Oid.FriendlyName, KeySize(request.PublicKey));
        }
        catch (CryptographicException e)
        {
            // The card signed it, so this failing means the file is damaged -
            // worth saying out loud before CertEnroll is blamed for refusing it.
            log.Problem($"Zadania nie da sie wczytac ({e.Message}). CertEnroll tez go nie przyjmie.", e);
        }
    }

    private static int KeySize(PublicKey key)
    {
        using var rsa = key.GetRSAPublicKey();
        return rsa?.KeySize ?? 0;
    }

    private static string? TemplateOf(X509Certificate2 certificate) =>
        certificate.Extensions["1.3.6.1.4.1.311.21.7"] is null ? "(bez rozszerzenia szablonu)" : "jest";

    /// <summary>The HRESULTs the lab has already seen, so the log names them.</summary>
    private static string Known(int hresult) => hresult switch
    {
        unchecked((int)0x800706BA) => "RPC_S_SERVER_UNAVAILABLE - brak tozsamosci domenowej albo CA",
        unchecked((int)0x80070005) => "E_ACCESSDENIED - konto nie ma prawa",
        unchecked((int)0x80070002) => "ERROR_FILE_NOT_FOUND - nie ma takiego CA",
        unchecked((int)0x80092009) => "CRYPT_E_NO_MATCH - nie ma pasujacego certyfikatu",
        unchecked((int)0x8009000F) => "NTE_EXISTS",
        _ => "nieznany tutaj",
    };

    private static string Mark(bool value) => value ? "[ok]" : "[!!]";
}

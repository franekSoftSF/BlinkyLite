using System.Security.Cryptography.X509Certificates;
using System.Text;
using BlinkyLite.Piv;

namespace BlinkyLite.CardLab;

/// <summary>
/// Every PIV data object that can be read without a PIN, raw and decoded.
/// </summary>
/// <remarks>
/// For 0026: a card Windows logs in with and a card it does not, compared byte
/// by byte. Nothing is written and no PIN is asked for. The output carries the
/// card's serial, its CHUID and certificates - public material, but it names
/// one physical token, so it goes to the results share and not to the
/// repository.
/// </remarks>
internal static class Dump
{
    private static readonly (string Name, byte[] Tag)[] Objects =
    [
        ("CCC (Card Capability Container)", [0x5F, 0xC1, 0x07]),
        ("CHUID", [0x5F, 0xC1, 0x02]),
        ("Discovery Object", [0x7E]),
        ("Key History", [0x5F, 0xC1, 0x0C]),
        ("Security Object", [0x5F, 0xC1, 0x06]),
        ("certyfikat 9A (PIV Authentication)", [0x5F, 0xC1, 0x05]),
        ("certyfikat 9C (Digital Signature)", [0x5F, 0xC1, 0x0A]),
        ("certyfikat 9D (Key Management)", [0x5F, 0xC1, 0x0B]),
        ("certyfikat 9E (Card Authentication)", [0x5F, 0xC1, 0x01]),
        ("ADMIN DATA (Yubico)", [0x5F, 0xFF, 0x00]),
        ("PRINTED (za PIN-em)", [0x5F, 0xC1, 0x09]),
    ];

    public static async Task<int> RunAsync(Options options, Transcript log)
    {
        using var card = CardScope.Open(options, log);
        if (card is null)
        {
            return 3;
        }

        var session = card.Session;
        log.Say($"serial:       {session.GetSerialNumber()}");
        log.Say($"firmware:     {session.GetFirmwareVersion()}");
        log.Say(string.Empty);

        var text = new StringBuilder();

        foreach (var (name, tag) in Objects)
        {
            var result = session.ReadRawObject(tag);

            log.Say($"{name}  [{Convert.ToHexString(tag)}]");

            if (!result.Present)
            {
                log.Say($"    brak (SW {result.Status:X4})");
                log.Say(string.Empty);
                continue;
            }

            log.Say($"    {result.Data.Length} bajtow");

            foreach (var line in Describe(tag, result.Data))
            {
                log.Say($"    {line}");
            }

            text.AppendLine($"{name} [{Convert.ToHexString(tag)}]");
            text.AppendLine(Convert.ToHexString(result.Data));
            text.AppendLine();
            log.Say(string.Empty);
        }

        var file = Path.Combine(options.OutDirectory, $"obiekty-{session.GetSerialNumber()}-{DateTime.Now:yyyyMMdd-HHmmss}.txt");
        await File.WriteAllTextAsync(file, log.ToString() + Environment.NewLine + "SUROWE" + Environment.NewLine + text);
        Console.WriteLine($"zrzut:        {file}");

        return 0;
    }

    /// <summary>What a person would want to see of each object, without a TLV viewer.</summary>
    private static IEnumerable<string> Describe(byte[] tag, byte[] data)
    {
        var body = Unwrap(data);

        if (tag.SequenceEqual(new byte[] { 0x5F, 0xC1, 0x02 }))
        {
            var fields = Tlv.ParseBer(body);

            if (fields.TryGetValue(0x34, out var guid) && guid.Length == 16)
            {
                var version = guid[6] >> 4;
                var variant = (guid[8] & 0xC0) == 0x80 ? "RFC 4122" : "INNY (nie RFC 4122)";
                yield return $"GUID:        {Convert.ToHexString(guid)}  wersja {version}, wariant {variant}";
            }

            if (fields.TryGetValue(0x30, out var fascn))
            {
                yield return $"FASC-N:      {Convert.ToHexString(fascn)}";
            }

            if (fields.TryGetValue(0x35, out var expiry))
            {
                yield return $"wygasa:      {Encoding.ASCII.GetString(expiry)}";
            }

            if (fields.TryGetValue(0x3E, out var signature))
            {
                yield return $"podpis 3E:   {signature.Length} bajtow";
            }

            yield break;
        }

        if (tag.Length == 3 && tag[0] == 0x5F && tag[1] == 0xC1
            && tag[2] is 0x05 or 0x0A or 0x0B or 0x01)
        {
            var fields = Tlv.ParseBer(body);
            if (fields.TryGetValue(0x70, out var der))
            {
                var (certificate, problem) = Load(der);
                if (problem is not null)
                {
                    yield return $"certyfikat nieczytelny: {problem}";
                }

                if (certificate is not null)
                {
                    yield return $"podmiot:     {certificate.Subject}";
                    yield return $"odcisk:      {certificate.Thumbprint}";
                    yield return $"wazny:       {certificate.NotBefore:yyyy-MM-dd} - {certificate.NotAfter:yyyy-MM-dd}";
                }
            }

            if (fields.TryGetValue(0x71, out var info))
            {
                yield return $"CertInfo 71: {Convert.ToHexString(info)}  ({(info.Length > 0 && info[0] == 0 ? "bez kompresji" : "SKOMPRESOWANY albo inny")})";
            }

            yield break;
        }

        yield return Convert.ToHexString(body.Length > 64 ? body[..64] : body) + (body.Length > 64 ? " ..." : string.Empty);
    }

    private static (X509Certificate2? Certificate, string? Problem) Load(byte[] der)
    {
        try
        {
            return (X509CertificateLoader.LoadCertificate(der), null);
        }
        catch (System.Security.Cryptography.CryptographicException e)
        {
            return (null, e.Message);
        }
    }

    /// <summary>GET DATA answers inside a 53 wrapper; the Discovery Object is the exception.</summary>
    private static byte[] Unwrap(byte[] data)
    {
        try
        {
            var outer = Tlv.ParseBer(data);
            return outer.TryGetValue(0x53, out var inner) ? inner : data;
        }
        catch (Exception e) when (e is ArgumentException or InvalidOperationException or IndexOutOfRangeException)
        {
            return data;
        }
    }
}

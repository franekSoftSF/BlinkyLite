using System.Formats.Asn1;
using System.Security.Cryptography;
using System.Security.Cryptography.Pkcs;
using System.Text;

namespace BlinkyLite.Issuance.Eobo;

/// <summary>
/// What is actually inside a CMC, read back from the bytes.
/// </summary>
/// <remarks>
/// <para>
/// A CMC that encodes without error still decides who gets the certificate,
/// and that decision lives in one place: the <c>RegInfo</c> control carrying
/// <c>requestername=DOMAIN\user</c>. Without it the CA has nobody to build the
/// subject for except whoever called it, and the certificate quietly comes out
/// in the operator's name (docs/02, krok 2). So the probe does not report
/// "Encode succeeded" - it reports what the encoded thing says.
/// </para>
/// <para>
/// Read with <see cref="AsnReader"/> because .NET has no CMC type. Everything
/// here is public material: a request, a requester name and certificates.
/// </para>
/// </remarks>
public static class CmcInspection
{
    /// <summary>id-cct-PKIData - the content type of a CMC full PKI request.</summary>
    public const string PkiDataContentType = "1.3.6.1.5.5.7.12.2";

    /// <summary>id-cmc-regInfo - where MS-WCCE puts the requester name.</summary>
    public const string RegInfoControl = "1.3.6.1.5.5.7.7.18";

    public sealed record Signer(string Subject, string DigestAlgorithm, bool HasCertificate);

    /// <param name="RequesterName">The name the CA will build the subject for, or null when nothing carries one.</param>
    /// <param name="Problem">Why the reading stopped, when it did.</param>
    public sealed record Contents(
        string ContentType,
        bool IsPkiData,
        IReadOnlyList<Signer> Signers,
        string? RequesterName,
        IReadOnlyList<string> Controls,
        string? Problem)
    {
        /// <summary>
        /// MS-WCCE wants at least two SignerInfos: the enrollee's (or a
        /// no-signature one standing in for it) and the enrolment agent's.
        /// </summary>
        public bool HasEnoughSigners => Signers.Count >= 2;
    }

    public static Contents Inspect(byte[] cmc)
    {
        ArgumentNullException.ThrowIfNull(cmc);

        var signed = new SignedCms();

        try
        {
            signed.Decode(cmc);
        }
        catch (Exception e) when (e is CryptographicException or AsnContentException)
        {
            return new Contents("?", false, [], null, [], $"SignedCms.Decode: {e.Message}");
        }

        var signers = new List<Signer>();

        try
        {
            foreach (var info in signed.SignerInfos)
            {
                signers.Add(new Signer(
                    info.Certificate?.Subject ?? "(bez certyfikatu w kopercie)",
                    info.DigestAlgorithm.FriendlyName ?? info.DigestAlgorithm.Value ?? "?",
                    info.Certificate is not null));
            }
        }
        catch (CryptographicException e)
        {
            // A SignerInfo with no signature - the stand-in MS-WCCE allows -
            // is exactly the kind of thing that trips a strict reader.
            signers.Add(new Signer($"(nie da sie odczytac: {e.Message})", "?", false));
        }

        var contentType = signed.ContentInfo.ContentType.Value ?? "?";
        var isPkiData = contentType == PkiDataContentType;

        if (!isPkiData)
        {
            return new Contents(contentType, false, signers, null, [],
                $"tresc to {contentType}, a nie PKIData ({PkiDataContentType})");
        }

        try
        {
            var (requester, controls) = ReadControls(signed.ContentInfo.Content);
            return new Contents(contentType, true, signers, requester, controls, null);
        }
        catch (AsnContentException e)
        {
            return new Contents(contentType, true, signers, null, [], $"PKIData: {e.Message}");
        }
    }

    /// <summary>
    /// The control sequence of a PKIData: every control's OID, and the value of
    /// the one that names the requester.
    /// </summary>
    private static (string? Requester, IReadOnlyList<string> Controls) ReadControls(byte[] pkiData)
    {
        var body = new AsnReader(pkiData, AsnEncodingRules.DER).ReadSequence();
        var controlSequence = body.ReadSequence();

        string? requester = null;
        var controls = new List<string>();

        while (controlSequence.HasData)
        {
            // TaggedAttribute ::= SEQUENCE { bodyPartID, attrType, attrValues }
            var attribute = controlSequence.ReadSequence();
            attribute.ReadInteger();

            var type = attribute.ReadObjectIdentifier();
            controls.Add(type);

            var values = attribute.ReadSetOf();
            if (type != RegInfoControl || !values.HasData)
            {
                continue;
            }

            // MS-WCCE: an OCTET STRING of UTF-8 pairs joined by "&", of which
            // requestername is the one that matters.
            var text = Encoding.UTF8.GetString(values.ReadOctetString());
            requester = text
                .Split('&')
                .Select(pair => pair.Split('=', 2))
                .Where(pair => pair.Length == 2
                               && pair[0].Equals("requestername", StringComparison.OrdinalIgnoreCase))
                .Select(pair => pair[1])
                .FirstOrDefault() ?? text;
        }

        return (requester, controls);
    }
}

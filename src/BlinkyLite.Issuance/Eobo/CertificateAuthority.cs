using System.Security.Cryptography.X509Certificates;

namespace BlinkyLite.Issuance.Eobo;

/// <summary>What the CA did with a request.</summary>
public enum CaDisposition
{
    Incomplete = 0,
    Error = 1,
    Denied = 2,
    Issued = 3,
    IssuedOutOfBand = 4,
    UnderSubmission = 5,
    Revoked = 6,
}

/// <summary>
/// The CA's answer, kept whole.
/// </summary>
/// <param name="RequestId">
/// Written down before anything else happens: a certificate the CA issued and
/// nobody recorded is the one failure this whole design cannot recover from
/// (docs/02, krok 18).
/// </param>
/// <param name="Message">The CA's own words, not ours - they are what an administrator will search for.</param>
public sealed record CaAnswer(
    CaDisposition Disposition,
    int RequestId,
    string Message,
    int Status,
    byte[]? Certificate)
{
    public bool IsIssued => Disposition is CaDisposition.Issued or CaDisposition.IssuedOutOfBand;

    public bool IsPending => Disposition is CaDisposition.UnderSubmission;

    public string StatusText => Status == 0 ? "-" : $"0x{Status:X8}";
}

/// <summary>
/// Microsoft ADCS, reached the only way it can be reached from a workstation:
/// <c>ICertRequest3</c> over DCOM, as the operator who is signed in (D-04).
/// </summary>
/// <remarks>
/// <para>
/// The call runs as the logged-on user, which is the point - the CA's own
/// access check is the thing that decides whether an enrolment agent may ask
/// on somebody's behalf, and it can only do that if a domain identity is
/// making the call. A service account or a container cannot.
/// </para>
/// <para>
/// The request id is read even when the CA refuses, because a refusal is a row
/// in the CA's database too, and that row is the only handle anybody has on it
/// afterwards.
/// </para>
/// </remarks>
public sealed class CertificateAuthority(string config)
{
    private const int InBase64 = 0x1;

    /// <summary>CR_IN_CMC: the request is a full CMC, not a bare PKCS#10.</summary>
    private const int InCmc = 0x400;

    private const int OutBase64 = 0x1;

    /// <summary>The CA configuration, <c>HOST\CA CN</c>, exactly as certutil prints it.</summary>
    public string Config { get; } = config;

    /// <summary>Sends the CMC and reports what the CA said.</summary>
    /// <param name="cmc">Base64 of the CMC the enrolment agent signed.</param>
    /// <param name="template">The template NAME; the CA compares the name, not the display name.</param>
    public CaAnswer Submit(string cmc, string template)
    {
        object? request = null;

        try
        {
            request = Com.Create("CertificateAuthority.Request");

            var disposition = (CaDisposition)Convert.ToInt32(
                Com.Invoke(request, "Submit", InBase64 | InCmc, cmc, $"CertificateTemplate:{template}", Config));

            return Answer(request, disposition);
        }
        finally
        {
            Com.Release(request);
        }
    }

    /// <summary>
    /// Asks again about a request the CA left for a manager to approve.
    /// </summary>
    /// <remarks>
    /// The station may be a different one, and the operator a different person:
    /// everything this needs is the request id, which is why it is written down
    /// before the answer is known.
    /// </remarks>
    public CaAnswer Retrieve(int requestId)
    {
        object? request = null;

        try
        {
            request = Com.Create("CertificateAuthority.Request");

            var disposition = (CaDisposition)Convert.ToInt32(
                Com.Invoke(request, "RetrievePending", requestId, Config));

            return Answer(request, disposition);
        }
        finally
        {
            Com.Release(request);
        }
    }

    private static CaAnswer Answer(object request, CaDisposition disposition)
    {
        var requestId = Convert.ToInt32(Com.Invoke(request, "GetRequestId"));
        var message = Com.Invoke(request, "GetDispositionMessage") as string ?? "";
        var status = Convert.ToInt32(Com.Invoke(request, "GetLastStatus"));

        byte[]? certificate = null;
        if (disposition is CaDisposition.Issued or CaDisposition.IssuedOutOfBand)
        {
            certificate = Convert.FromBase64String(
                (string)Com.Invoke(request, "GetCertificate", OutBase64)!);
        }

        return new CaAnswer(disposition, requestId, message, status, certificate);
    }

    /// <summary>
    /// The errors this lab has met, said in a sentence instead of a number.
    /// </summary>
    /// <remarks>
    /// Every one of these was once read as a fault in the card or in the code.
    /// They are none of them that: they are the CA, the domain or the template
    /// saying no.
    /// </remarks>
    public static string Explain(int status) => status switch
    {
        unchecked((int)0x80094012) => "CERTSRV_E_TEMPLATE_DENIED - uprawnienia na szablonie nie pozwalaja na to "
                                      + "zadanie (przy EOBO licza sie prawa agenta)",
        unchecked((int)0x80094011) => "CERTSRV_E_SUBJECT_EMAIL_REQUIRED albo brak wymaganej wartosci w podmiocie",
        unchecked((int)0x80094800) => "CERTSRV_E_UNSUPPORTED_CERT_TYPE - CA nie zna tego szablonu",
        unchecked((int)0x80094811) => "CERTSRV_E_KEY_LENGTH - klucz krotszy, niz szablon wymaga",
        unchecked((int)0x8009480F) => "CERTSRV_E_ENROLL_DENIED - zadanie odrzucone przez polityke",
        unchecked((int)0x800706BA) => "RPC_S_SERVER_UNAVAILABLE - brak tozsamosci domenowej albo CA nieosiagalne",
        unchecked((int)0x80070005) => "E_ACCESSDENIED - konto nie ma prawa wolac tego CA",
        unchecked((int)0x80070002) => "ERROR_FILE_NOT_FOUND - nie ma takiej konfiguracji CA",
        _ => "nieznany tutaj",
    };

    /// <summary>The certificate the CA issued, as a certificate.</summary>
    public static X509Certificate2 Read(byte[] der) => X509CertificateLoader.LoadCertificate(der);
}

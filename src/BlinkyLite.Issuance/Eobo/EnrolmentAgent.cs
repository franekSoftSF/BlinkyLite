using System.Security.Cryptography.X509Certificates;

namespace BlinkyLite.Issuance.Eobo;

/// <summary>
/// The operator's enrolment agent certificate: the one the CA accepts as
/// "this person may ask on somebody else's behalf".
/// </summary>
/// <remarks>
/// Looked for in <c>CurrentUser\My</c> and nowhere else. The agent signs as a
/// human, so the key belongs to that human's profile or to the card in their
/// reader; a machine store would mean the station can enrol on anybody's
/// behalf whether or not a person is sitting at it.
/// </remarks>
public static class EnrolmentAgent
{
    /// <summary>EKU Certificate Request Agent - what makes a certificate an EA one.</summary>
    public const string CertificateRequestAgentOid = "1.3.6.1.4.1.311.20.2.1";

    /// <summary>What one candidate looks like, before anything is chosen.</summary>
    public sealed record Candidate(
        X509Certificate2 Certificate,
        bool HasPrivateKey,
        bool IsCurrent)
    {
        public string Thumbprint => Certificate.Thumbprint;

        public string Subject => Certificate.Subject;

        /// <summary>True when this one can actually sign today.</summary>
        public bool IsUsable => HasPrivateKey && IsCurrent;
    }

    /// <summary>
    /// Every EA certificate in the operator's store, usable or not.
    /// </summary>
    /// <remarks>
    /// Unusable ones are returned rather than filtered out: "you have an agent
    /// certificate but it expired last week" is an answer somebody can act on,
    /// and an empty list is not.
    /// </remarks>
    public static IReadOnlyList<Candidate> Find()
    {
        using var store = new X509Store(StoreName.My, StoreLocation.CurrentUser);
        store.Open(OpenFlags.ReadOnly);

        var now = DateTime.Now;
        var found = new List<Candidate>();

        foreach (var certificate in store.Certificates)
        {
            if (!HasAgentEku(certificate))
            {
                certificate.Dispose();
                continue;
            }

            found.Add(new Candidate(
                certificate,
                certificate.HasPrivateKey,
                now >= certificate.NotBefore && now <= certificate.NotAfter));
        }

        return found;
    }

    private static bool HasAgentEku(X509Certificate2 certificate) =>
        certificate.Extensions
            .OfType<X509EnhancedKeyUsageExtension>()
            .Any(eku => eku.EnhancedKeyUsages
                .OfType<System.Security.Cryptography.Oid>()
                .Any(oid => oid.Value == CertificateRequestAgentOid));
}

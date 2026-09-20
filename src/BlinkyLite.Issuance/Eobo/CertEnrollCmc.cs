using System.Security.Cryptography.X509Certificates;

namespace BlinkyLite.Issuance.Eobo;

/// <summary>A step of the CMC build failed, with the HRESULT the COM object gave.</summary>
public sealed class CertEnrollException : Exception
{
    public CertEnrollException(string step, string detail, int hresult)
        : base($"{step}: {detail}")
    {
        Step = step;

        // Kept where every other .NET exception keeps it, so a caller that
        // knows nothing about CertEnroll still finds the number.
        if (hresult != 0)
        {
            HResult = hresult;
        }
    }

    public string Step { get; }

    /// <summary>The HRESULT as it is written down in Microsoft's errors, or "-".</summary>
    public string HResultText => HResult == 0 ? "-" : $"0x{HResult:X8}";
}

/// <summary>
/// Wraps a PKCS#10 the card signed in a CMC the CA accepts on somebody else's
/// behalf, using the CertEnroll COM that ships with Windows (D-04).
/// </summary>
/// <remarks>
/// <para>
/// Late-bound on purpose. A COM interop assembly would tie the build to one
/// Windows SDK and would have to be published for two architectures; the
/// objects here are IDispatch, and <see cref="Type.InvokeMember(string, BindingFlags, System.Reflection.Binder, object, object[])"/>
/// speaks to them without any of that.
/// </para>
/// <para>
/// The open question this class exists to answer (Q-01) is whether
/// <c>InitializeFromInnerRequest</c> accepts a PKCS#10 whose private key this
/// machine does not have and never will - the key is on the cardholder's
/// token. If it refuses, the CMC gets written by hand instead, the way the
/// sibling project does it.
/// </para>
/// </remarks>
public static class CertEnrollCmc
{
    private const int Base64 = 1;

    /// <summary>Let Windows ask for the PIN when the agent's key is on a card.</summary>
    private const int VerifyAllowUi = 4;

    /// <summary>
    /// <c>ISignerCertificate::Initialize</c> takes a MachineContext boolean,
    /// not an enrolment context: false means the agent's key is looked for in
    /// the person's store, which is the only place it may live.
    /// </summary>
    private const int UserContext = 0;

    /// <summary>What the build produced, or how far it got.</summary>
    public sealed record Attempt(
        bool InnerRequestAccepted,
        bool RequesterNameAccepted,
        bool SignerAccepted,
        bool Encoded,
        string? Cmc,
        CertEnrollException? Failure)
    {
        public bool Succeeded => Encoded && Cmc is not null;
    }

    /// <summary>
    /// Builds the CMC, step by step, and reports which step was the last one
    /// that worked.
    /// </summary>
    /// <param name="pkcs10">DER of the request the card signed.</param>
    /// <param name="requesterName"><c>DOMAIN\sAMAccountName</c> of the cardholder.</param>
    /// <param name="agent">The operator's enrolment agent certificate.</param>
    public static Attempt Build(byte[] pkcs10, string requesterName, X509Certificate2 agent)
    {
        ArgumentNullException.ThrowIfNull(pkcs10);
        ArgumentNullException.ThrowIfNull(agent);

        // Exactly one backslash, checked before the CA is given a chance to
        // silently build the subject from whoever called it (docs/02, step 2).
        if (requesterName.Count(c => c == '\\') != 1)
        {
            throw new ArgumentException(
                $"RequesterName must be DOMAIN\\user with exactly one backslash, not \"{requesterName}\".",
                nameof(requesterName));
        }

        object? inner = null;
        object? cmc = null;
        object? signer = null;

        var innerOk = false;
        var nameOk = false;
        var signerOk = false;

        try
        {
            inner = Com.Create("X509Enrollment.CX509CertificateRequestPkcs10");
            Com.Invoke(inner, "InitializeDecode", Convert.ToBase64String(pkcs10), Base64);
            innerOk = true;

            cmc = Com.Create("X509Enrollment.CX509CertificateRequestCmc");
            Com.Invoke(cmc, "InitializeFromInnerRequest", inner);

            Com.Set(cmc, "RequesterName", requesterName);
            nameOk = true;

            signer = Com.Create("X509Enrollment.CSignerCertificate");
            Com.Invoke(signer, "Initialize",
                UserContext, VerifyAllowUi, Base64, Convert.ToBase64String(agent.RawData));
            Com.Set(cmc, "SignerCertificate", signer);
            signerOk = true;

            Com.Invoke(cmc, "Encode");
            var encoded = (string)Com.Get(cmc, "RawData", Base64)!;

            return new Attempt(true, true, true, true, encoded, null);
        }
        catch (CertEnrollException e)
        {
            return new Attempt(innerOk, nameOk, signerOk, false, null, e);
        }
        finally
        {
            Com.Release(signer);
            Com.Release(cmc);
            Com.Release(inner);
        }
    }
}

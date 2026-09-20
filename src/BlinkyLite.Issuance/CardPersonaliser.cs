using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using BlinkyLite.Contracts;
using BlinkyLite.Piv;
using BlinkyLite.Piv.Attestation;

namespace BlinkyLite.Issuance;

/// <summary>Asks the user - the person the key is for - to set a PIN.</summary>
/// <remarks>
/// An interface, because the PIN is typed by a human and the two shells put
/// that human in front of two different things: a window in WPF, a masked
/// prompt in PowerShell. The engine never reads the console itself.
/// </remarks>
public interface IPinPrompt
{
    /// <summary>The new PIN, or null when the person gave up.</summary>
    Task<string?> AskAsync(PinPromptContext context, CancellationToken ct = default);
}

/// <param name="RefusalKey">Why the previous answer was refused; null on the first ask.</param>
public sealed record PinPromptContext(
    uint? Serial,
    PinComplexityPolicy Policy,
    int Attempt,
    string? RefusalKey);

/// <summary>What the server reserved for this card before anything was written to it (D-03).</summary>
public sealed record PersonalisationRequest(
    byte[] ManagementKey,
    string Puk,
    PivAlgorithm KeyAlgorithm = PivAlgorithm.Rsa2048,
    PinPolicy PinPolicy = PinPolicy.Once,
    TouchPolicy TouchPolicy = TouchPolicy.Never,
    PinComplexityPolicy? PinComplexity = null)
{
    public PinComplexityPolicy Complexity => PinComplexity ?? PinComplexityPolicy.Default;
}

/// <summary>What the card produced; everything here goes to the server.</summary>
public sealed record PersonalisationResult(
    uint Serial,
    FirmwareVersion Firmware,
    PivAlgorithm ManagementKeyAlgorithm,
    byte[] AttestationDer,
    byte[] IntermediateDer,
    byte[] CsrDer,
    YubicoAttestation Attestation,
    bool WroteChuid,
    bool WroteCcc);

/// <summary>The card cannot be personalised, and why - before anything was written.</summary>
public sealed class PersonalisationRefusedException(string messageKey, string detail)
    : Exception($"{messageKey}: {detail}")
{
    public string MessageKey { get; } = messageKey;
}

/// <summary>
/// Turns a factory YubiKey into one BlinkyLite owns: its management key, its
/// PUK, the user's PIN, a key in slot 9A and a request the card signed itself.
/// </summary>
/// <remarks>
/// <para>
/// The order is fixed and every step of it comes from something that went
/// wrong once (docs/02, docs/06): the management key first and into PRINTED
/// with the ADMIN DATA flag, or Yubico's minidriver takes the card over; the
/// PUK before the PIN; CHUID and CCC before Windows is asked to log anybody
/// in; and the whole thing inside one PC/SC transaction, because a background
/// poll in the middle produces SCARD_E_SHARING_VIOLATION.
/// </para>
/// <para>
/// The PIN is asked for, used, and dropped. It is not returned, not logged and
/// not stored - there is nowhere in BlinkyLite that a PIN can live.
/// </para>
/// </remarks>
public sealed class CardPersonaliser(AttestationVerifier? verifier = null)
{
    private const int PinAttempts = 3;

    private readonly AttestationVerifier attestation = verifier ?? AttestationVerifier.ForYubico();

    public async Task<PersonalisationResult> PersonaliseAsync(
        PivSession session,
        PersonalisationRequest request,
        IPinPrompt prompt,
        string subject,
        IProgress<string>? progress = null,
        CancellationToken ct = default)
    {
        progress?.Report("issuance.step.read-card");
        session.Select();

        var serial = session.GetSerialNumber()
            ?? throw new PersonalisationRefusedException("error.card.not-a-yubikey",
                "the card has no serial number, so it is not a YubiKey");

        var firmware = session.GetFirmwareVersion();
        var management = session.GetManagementKeyMetadata()
            ?? throw new PersonalisationRefusedException("error.card.too-old",
                $"firmware {firmware} cannot report its management key algorithm (5.3 or later can)");

        var puk = session.GetPukMetadata();
        if (puk.IsUnrecoverable)
        {
            throw new PersonalisationRefusedException("error.card.no-puk",
                "the card has no PUK; BlinkyLite does not issue to a card it could never unblock");
        }

        // 0011 personalises a factory card. A card BlinkyLite already knows is
        // resumed through the management key candidates (0022), and a card with
        // an unknown key is refused rather than reset.
        if (!management.IsDefault)
        {
            throw new PersonalisationRefusedException("error.card.not-factory",
                "the management key is not the factory one; reset the card or resume its issuance");
        }

        if (session.GetSlotMetadata(PivSlot.Authentication) is not null)
        {
            throw new PersonalisationRefusedException("error.card.slot-occupied",
                "slot 9A already holds a key");
        }

        // One transaction for the whole sequence: a reader poll between two of
        // these steps is what produces SCARD_E_SHARING_VIOLATION.
        using var transaction = session.Connection.BeginTransaction();

        progress?.Report("issuance.step.personalise");
        session.AuthenticateManagementKey(ManagementKey.Default(management.Algorithm));

        var newKey = new ManagementKey(request.ManagementKey, management.Algorithm);
        session.SetManagementKey(newKey, alsoBehindPin: true);
        session.ChangePuk(PinComplexityPolicy.FactoryPuk, request.Puk);

        progress?.Report("issuance.step.pin");
        var pin = await AskForPinAsync(prompt, serial, request, ct);
        try
        {
            session.ChangePin(PinComplexityPolicy.FactoryPin, pin);
            session.VerifyPin(pin);

            // Without CHUID and CCC Windows answers NTE_BAD_KEYSET and the card
            // cannot log anybody in, however good the certificate is.
            var identity = session.EnsureCardIdentity(DateOnly.FromDateTime(DateTime.UtcNow.AddYears(10)));

            progress?.Report("issuance.step.generate-key");
            var publicKey = session.GenerateKeyPair(
                PivSlot.Authentication, request.KeyAlgorithm, request.PinPolicy, request.TouchPolicy);

            progress?.Report("issuance.step.attest");
            var leaf = session.Attest(PivSlot.Authentication)
                ?? throw new PersonalisationRefusedException("error.card.no-attestation",
                    "the card did not return an attestation for slot 9A");
            var intermediate = session.GetAttestationCertificate()
                ?? throw new PersonalisationRefusedException("error.card.no-attestation",
                    "the card has no attestation certificate in F9");

            var verdict = attestation.Verify(leaf, intermediate, PivSlot.Authentication, serial,
                publicKey.SubjectPublicKeyInfo);
            if (!verdict.IsTrusted)
            {
                throw new PersonalisationRefusedException("error.card.attestation-refused", verdict.Explanation);
            }

            progress?.Report("issuance.step.sign-request");
            var csr = SignRequest(session, publicKey, subject, pin);

            progress?.Report("issuance.step.done");
            return new PersonalisationResult(
                serial, firmware, management.Algorithm,
                leaf.RawData, intermediate.RawData, csr, verdict.Attestation!,
                identity.Chuid, identity.CapabilityContainer);
        }
        finally
        {
            // The PIN existed in this process for as long as the card needed it
            // and not one step longer.
            pin.AsSpan().Clear();
        }
    }

    private static byte[] SignRequest(PivSession session, PivPublicKey publicKey, string subject, char[] pin)
    {
        var generator = new PivSignatureGenerator(session, PivSlot.Authentication, publicKey);
        var request = new CertificateRequest(
            new X500DistinguishedName(subject), generator.PublicKey, HashAlgorithmName.SHA256);

        try
        {
            return request.CreateSigningRequest(generator);
        }
        catch (PivException) when (RetryAfterPin(session, pin))
        {
            // YubiKey 5.8.0: a PIN verified earlier in the session no longer
            // satisfies a key generated after it, and signing answers 6982.
            // One more VERIFY, and only one - the PIN is already in hand, so
            // this costs the user nothing and saves a whole issuance.
            return request.CreateSigningRequest(generator);
        }
    }

    private static bool RetryAfterPin(PivSession session, char[] pin)
    {
        try
        {
            session.VerifyPin(pin);
            return true;
        }
        catch (PivException)
        {
            return false;
        }
    }

    private static async Task<char[]> AskForPinAsync(
        IPinPrompt prompt, uint serial, PersonalisationRequest request, CancellationToken ct)
    {
        string? refusal = null;

        for (var attempt = 1; attempt <= PinAttempts; attempt++)
        {
            var answer = await prompt.AskAsync(
                new PinPromptContext(serial, request.Complexity, attempt, refusal), ct);

            if (answer is null)
            {
                throw new PersonalisationRefusedException("error.pin.cancelled",
                    "the user did not set a PIN");
            }

            // Checked here and not only in the window: the PowerShell module
            // puts a different window in front of the same engine.
            var verdict = PinRules.Check(answer, request.Complexity, serial, request.Puk);
            if (verdict.IsAcceptable)
            {
                return answer.ToCharArray();
            }

            refusal = verdict.MessageKey;
        }

        throw new PersonalisationRefusedException("error.pin.cancelled",
            $"no acceptable PIN after {PinAttempts} attempts");
    }
}

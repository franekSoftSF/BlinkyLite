using System.Formats.Asn1;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using BlinkyLite.Contracts;
using BlinkyLite.Piv;
using BlinkyLite.Piv.Attestation;
using BlinkyLite.Server.Auth;
using BlinkyLite.Server.Data;
using BlinkyLite.Server.Secrets;
using Microsoft.Extensions.Options;

namespace BlinkyLite.Server.Issuing;

/// <summary>A request the server refuses, with the key the client will show.</summary>
public sealed class IssuanceRefusedException(int status, string messageKey, string detail)
    : Exception($"{messageKey}: {detail}")
{
    public int Status { get; } = status;

    public string MessageKey { get; } = messageKey;
}

/// <summary>
/// The server's half of an issuance: it makes the secrets, believes nothing the
/// station says about the card, and closes the issuance only when the
/// certificate matches what the card proved.
/// </summary>
/// <remarks>
/// <para>
/// Every write goes through <see cref="IProcedures"/>; reads use a read-only
/// NHibernate session. Nothing here writes SQL.
/// </para>
/// <para>
/// The station verifies the attestation too, and that is not a duplicate: the
/// station checks so it can stop before wasting a card, the server checks
/// because a station is a machine on somebody's desk and its word is not
/// evidence (docs/02, krok 15).
/// </para>
/// </remarks>
public sealed class IssuanceService(
    IOptions<IssuanceOptions> options,
    IProcedures procedures,
    SecretEnvelopes envelopes,
    IDirectory directory,
    IIssuanceReader issuances,
    AttestationVerifier attestation,
    ILogger<IssuanceService> log)
{
    private const int PukDigits = 8;

    /// <summary>24 bytes: enough for 3DES and AES-192, cut to size by the station that asked the card.</summary>
    private const int ManagementKeyBytes = 24;

    /// <summary>szOID_NT_PRINCIPAL_NAME - the UPN inside a subject alternative name.</summary>
    private const string UpnOid = "1.3.6.1.4.1.311.20.2.3";

    public IReadOnlyList<IssuanceProfile> Profiles() => options.Value.Published();

    /// <summary>
    /// Makes the PUK and the management key, seals them, and writes the
    /// reservation - in that order (D-03).
    /// </summary>
    /// <remarks>
    /// The secret exists in the database before it exists on the card. The
    /// other order produces a card nobody can open: the write to the token
    /// succeeds, the write to the database does not, and the management key is
    /// gone.
    /// </remarks>
    public async Task<ReservationResponse> ReserveAsync(
        StartIssuanceRequest request, Actor actor, string windowsIdentity, CancellationToken ct = default)
    {
        var profile = options.Value.Find(request.ProfileName)
            ?? throw new IssuanceRefusedException(StatusCodes.Status400BadRequest, ErrorCodes.ProfileUnknown,
                $"no profile called \"{request.ProfileName}\" is configured on this server");

        var target = await directory.FindBySidAsync(request.TargetSid, ct)
            ?? throw new IssuanceRefusedException(StatusCodes.Status404NotFound, ErrorCodes.TargetNotFound,
                $"no account in AD has the SID {request.TargetSid}");

        if (!target.Enabled)
        {
            throw new IssuanceRefusedException(StatusCodes.Status400BadRequest, ErrorCodes.TargetDisabled,
                $"{target.SamAccount} is disabled in AD");
        }

        // The envelopes are bound to this id, so the id has to exist before
        // them - which is why the server chooses it (migration 0005).
        var issuanceId = Guid.CreateVersion7();
        var algorithm = ManagementKeyAlgorithm(request.ManagementKeyAlgorithm);

        var managementKey = RandomNumberGenerator.GetBytes(ManagementKeyBytes);
        var puk = request.HasPuk ? RandomDigits(PukDigits) : null;

        var reservation = new IssuanceReservation(
            issuanceId,
            request.CardSerial,
            request.Firmware,
            request.HasPuk,
            target.SamAccount,
            target.Upn,
            target.Sid,
            target.DisplayName,
            profile.Name,
            profile.Template,
            profile.CaConfig,
            windowsIdentity,
            request.Workstation,
            puk is null ? null : envelopes.Seal(
                System.Text.Encoding.ASCII.GetBytes(puk),
                new EnvelopeBinding(EnvelopeKind.Puk, request.CardSerial, issuanceId)),
            envelopes.Seal(
                managementKey,
                new EnvelopeBinding(EnvelopeKind.ManagementKey, request.CardSerial, issuanceId)),
            (byte)algorithm,
            envelopes.CurrentVersion);

        await procedures.ReserveIssuanceAsync(reservation, actor, ct);

        log.LogInformation("Issuance {Id} reserved for {Target} on card {Serial}, profile {Profile}",
            issuanceId, target.SamAccount, request.CardSerial, profile.Name);

        return new ReservationResponse(
            issuanceId, puk, Convert.ToBase64String(managementKey), profile, target);
    }

    public Task CustomisedAsync(Guid id, Actor actor, CancellationToken ct = default) =>
        procedures.MarkCustomisedAsync(id, actor, ct);

    /// <summary>
    /// Checks what the card proved about the key, and stores the proof.
    /// </summary>
    /// <remarks>
    /// Three things have to agree: the attestation chains to a pinned Yubico
    /// root, the serial in it is the card this issuance is about, and the key
    /// in the request is the key the attestation describes. A station that
    /// swapped the request for one whose private key it holds fails the third.
    /// </remarks>
    public async Task AttestAsync(Guid id, AttestationUpload upload, Actor actor, CancellationToken ct = default)
    {
        var issuance = Load(id);

        var request = ReadRequest(upload.Csr);
        var publicKey = request.PublicKey.ExportSubjectPublicKeyInfo();

        using var leaf = Certificate(upload.Attestation, "attestation");
        using var intermediate = Certificate(upload.Intermediate, "attestation intermediate");

        var verdict = attestation.Verify(
            leaf, intermediate, PivSlot.Authentication, (uint)issuance.CardSerial, publicKey);

        if (!verdict.IsTrusted)
        {
            log.LogWarning("Issuance {Id}: attestation refused - {Reason}", id, verdict.Explanation);

            throw new IssuanceRefusedException(StatusCodes.Status400BadRequest,
                ErrorCodes.CardAttestationRefused, verdict.Explanation);
        }

        var proof = verdict.Attestation!;

        await procedures.MarkAttestedAsync(id, new AttestationRecord(
            upload.Attestation,
            upload.Intermediate,
            upload.Csr,
            request.PublicKey.Oid.FriendlyName ?? request.PublicKey.Oid.Value ?? "?",
            (short)proof.PinPolicy,
            (short)proof.TouchPolicy,
            (short)proof.FormFactor), actor, ct);
    }

    public Task SubmittedAsync(Guid id, SubmittedRequest request, Actor actor, CancellationToken ct = default) =>
        procedures.MarkSubmittedAsync(id, request.CaRequestId, request.EnrolmentAgentThumbprint, actor, ct);

    public Task PendingAsync(Guid id, Actor actor, CancellationToken ct = default) =>
        procedures.MarkPendingAsync(id, actor, ct);

    public Task FailedAsync(Guid id, string error, Actor actor, CancellationToken ct = default) =>
        procedures.MarkFailedAsync(id, error, actor, ct);

    /// <summary>
    /// Closes the issuance, once the certificate is shown to belong to the key
    /// the card attested and to the person it was meant for.
    /// </summary>
    /// <remarks>
    /// Both checks matter for the same reason: everything between the request
    /// and this call happened on the station and at the CA. A certificate for
    /// the right person on a key nobody attested, or for the operator on the
    /// attested key, both look like success from there.
    /// </remarks>
    public async Task CompleteAsync(Guid id, byte[] certificateDer, Actor actor, CancellationToken ct = default)
    {
        var issuance = Load(id);

        if (issuance.CsrDer is null)
        {
            throw new IssuanceRefusedException(StatusCodes.Status409Conflict, ErrorCodes.IssuanceInvalidState,
                "this issuance has no attested request yet");
        }

        using var certificate = Certificate(certificateDer, "certificate");
        var attested = ReadRequest(issuance.CsrDer).PublicKey.ExportSubjectPublicKeyInfo();

        if (!certificate.PublicKey.ExportSubjectPublicKeyInfo().AsSpan().SequenceEqual(attested))
        {
            log.LogWarning("Issuance {Id}: the certificate is for a different key than the card attested", id);

            throw new IssuanceRefusedException(StatusCodes.Status400BadRequest, ErrorCodes.CertificateKeyMismatch,
                "the certificate's public key is not the key the card attested");
        }

        var upn = UpnOf(certificate);
        if (upn is not null && !upn.Equals(issuance.TargetUpn, StringComparison.OrdinalIgnoreCase))
        {
            log.LogWarning("Issuance {Id}: certificate names {Upn}, issuance targets {Target}",
                id, upn, issuance.TargetUpn);

            throw new IssuanceRefusedException(StatusCodes.Status400BadRequest, ErrorCodes.CertificateTargetMismatch,
                $"the certificate names {upn}, this issuance is for {issuance.TargetUpn}");
        }

        await procedures.MarkIssuedAsync(id, new IssuedCertificate(
            certificateDer,
            certificate.SerialNumber,
            certificate.Thumbprint,
            certificate.NotBefore.ToUniversalTime(),
            certificate.NotAfter.ToUniversalTime()), actor, ct);

        log.LogInformation("Issuance {Id} issued: {Subject}, valid to {NotAfter:yyyy-MM-dd}",
            id, certificate.Subject, certificate.NotAfter);
    }

    private Issuance Load(Guid id) =>
        issuances.Find(id)
        ?? throw new IssuanceRefusedException(StatusCodes.Status404NotFound, ErrorCodes.NotFound,
            $"no issuance {id}");

    /// <summary>
    /// Reads a PKCS#10. .NET checks the signature while parsing, so a request
    /// the card did not sign never gets past this line.
    /// </summary>
    private static CertificateRequest ReadRequest(byte[] der)
    {
        try
        {
            return CertificateRequest.LoadSigningRequest(der, HashAlgorithmName.SHA256);
        }
        catch (Exception e) when (e is CryptographicException or ArgumentException)
        {
            throw new IssuanceRefusedException(StatusCodes.Status400BadRequest, ErrorCodes.BadRequest,
                $"the certificate request cannot be read or is not signed by its own key: {e.Message}");
        }
    }

    private static X509Certificate2 Certificate(byte[] der, string what)
    {
        try
        {
            return X509CertificateLoader.LoadCertificate(der);
        }
        catch (CryptographicException e)
        {
            throw new IssuanceRefusedException(StatusCodes.Status400BadRequest, ErrorCodes.BadRequest,
                $"the {what} is not a certificate: {e.Message}");
        }
    }

    /// <summary>
    /// The UPN from the subject alternative name, or null when the certificate
    /// carries none.
    /// </summary>
    /// <remarks>
    /// Null is not a failure: a template may build the subject without a UPN,
    /// and the binding that matters for logging on is the SID extension the CA
    /// adds. What is refused is a certificate that names somebody <i>else</i>.
    /// </remarks>
    private static string? UpnOf(X509Certificate2 certificate)
    {
        // .NET reads DNS names and IP addresses out of a SAN, but a UPN is an
        // otherName, and there is no method for those - so the extension is
        // read by hand. GeneralName ::= otherName [0] IMPLICIT OtherName,
        // OtherName ::= SEQUENCE { type-id OID, value [0] EXPLICIT ANY }.
        var extension = certificate.Extensions["2.5.29.17"];
        if (extension is null)
        {
            return null;
        }

        try
        {
            var names = new AsnReader(extension.RawData, AsnEncodingRules.DER).ReadSequence();

            while (names.HasData)
            {
                var tag = names.PeekTag();
                if (tag.TagClass != TagClass.ContextSpecific || tag.TagValue != 0)
                {
                    names.ReadEncodedValue();
                    continue;
                }

                var other = names.ReadSequence(tag);
                if (other.ReadObjectIdentifier() != UpnOid)
                {
                    continue;
                }

                return other
                    .ReadSequence(new Asn1Tag(TagClass.ContextSpecific, 0, isConstructed: true))
                    .ReadCharacterString(UniversalTagNumber.UTF8String);
            }
        }
        catch (AsnContentException)
        {
            // A SAN that cannot be read names nobody, which is the same answer
            // as a certificate without one: not a match, not a refusal.
            return null;
        }

        return null;
    }

    private static PivAlgorithm ManagementKeyAlgorithm(string name) =>
        Enum.TryParse<PivAlgorithm>(name, ignoreCase: true, out var parsed)
            ? parsed
            : throw new IssuanceRefusedException(StatusCodes.Status400BadRequest, ErrorCodes.BadRequest,
                $"{name} is not a management key algorithm this server knows");

    /// <summary>
    /// A PUK of digits, drawn without modulo bias - every digit equally likely.
    /// </summary>
    private static string RandomDigits(int count) =>
        string.Concat(Enumerable.Range(0, count).Select(_ => RandomNumberGenerator.GetInt32(0, 10)));
}

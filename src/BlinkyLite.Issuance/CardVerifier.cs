using System.Security.Cryptography.X509Certificates;
using BlinkyLite.Contracts;
using BlinkyLite.Issuance.Api;
using BlinkyLite.Piv;
using BlinkyLite.Piv.Attestation;

namespace BlinkyLite.Issuance;

/// <summary>One line of a verification: a message key and whether it held.</summary>
/// <param name="Detail">What was compared, for the log and the details pane - never a secret.</param>
public sealed record VerificationCheck(string MessageKey, bool Passed, string? Detail = null);

public sealed record CardVerification(uint Serial, CardRecord? Record, IReadOnlyList<VerificationCheck> Checks)
{
    public bool Passed => Checks.Count > 0 && Checks.All(c => c.Passed);
}

/// <summary>
/// "Verify card" (0030): is the token in the reader the one the record says
/// was issued, with the key the record says it has? Read only - no PIN, no
/// management key, nothing written.
/// </summary>
/// <remarks>
/// <para>
/// Lives here and not in the WPF window or the cmdlet, because both show the
/// same answer and an answer computed twice is two answers.
/// </para>
/// <para>
/// A fresh attestation, not the stored one, is what proves anything: the
/// stored one says what the card was at issuance, the new one what it is now.
/// A key replaced since - a reset and a new key from anywhere else - makes a
/// new attestation with another public key, and that is the check that
/// catches it.
/// </para>
/// </remarks>
public static class CardVerifier
{
    public const string Known = "verify.known";
    public const string CertificateMatches = "verify.certificate";
    public const string AttestationTrusted = "verify.attestation";
    public const string KeyIsCertified = "verify.key-certified";
    public const string SameKeyAsIssued = "verify.same-key";

    /// <summary>Reads the card and asks the server; the checks are <see cref="Evaluate"/>.</summary>
    /// <exception cref="PersonalisationRefusedException">Not a YubiKey: there is no serial to look up.</exception>
    public static async Task<CardVerification> VerifyAsync(PivSession session, ServerClient server, CancellationToken ct = default)
    {
        var serial = session.GetSerialNumber()
            ?? throw new PersonalisationRefusedException(ErrorCodes.CardNotAYubiKey, "the token reports no serial number");

        var record = await server.CardAsync(serial, ct);
        var certificate = session.GetCertificate(PivSlot.Authentication);

        // Attest fails when slot 9A holds no key, or one that was imported
        // rather than generated on the card; both are answers, not errors.
        X509Certificate2? attestation = null;
        X509Certificate2? intermediate = null;
        try
        {
            attestation = session.Attest(PivSlot.Authentication);
            intermediate = session.GetAttestationCertificate();
        }
        catch (PivException)
        {
        }

        return Evaluate(serial, record, certificate, attestation, intermediate, AttestationVerifier.ForYubico());
    }

    public static CardVerification Evaluate(
        uint serial,
        CardRecord? record,
        byte[]? certificateOnCard,
        X509Certificate2? attestation,
        X509Certificate2? intermediate,
        AttestationVerifier verifier)
    {
        var checks = new List<VerificationCheck>();

        var issuance = record?.Issuance;
        checks.Add(new VerificationCheck(Known, issuance is { State: IssuanceState.Issued },
            issuance is null ? "no issuance on record for this serial" : $"{issuance.Id} {issuance.State}"));

        // Without a record there is nothing to compare the card with; more
        // lines would only be more ways of saying the same thing.
        if (issuance is null)
        {
            return new CardVerification(serial, record, checks);
        }

        checks.Add(new VerificationCheck(CertificateMatches,
            certificateOnCard is not null && record!.Certificate is not null
                && certificateOnCard.AsSpan().SequenceEqual(record.Certificate),
            certificateOnCard is null ? "slot 9A has no certificate" : Thumbprint(certificateOnCard)));

        var trusted = attestation is not null && intermediate is not null
            ? verifier.Verify(attestation, intermediate, PivSlot.Authentication, serial)
            : null;
        checks.Add(new VerificationCheck(AttestationTrusted, trusted?.IsTrusted == true,
            trusted?.ToString() ?? "slot 9A cannot be attested - no key, or a key that was not generated on the card"));

        var attestedKey = attestation?.PublicKey.EncodedKeyValue.RawData;
        var certifiedKey = certificateOnCard is null ? null : PublicKeyOf(certificateOnCard);
        checks.Add(new VerificationCheck(KeyIsCertified,
            attestedKey is not null && certifiedKey is not null && attestedKey.AsSpan().SequenceEqual(certifiedKey)));

        var recordedKey = record!.Attestation is null ? null : PublicKeyOf(record.Attestation);
        checks.Add(new VerificationCheck(SameKeyAsIssued,
            attestedKey is not null && recordedKey is not null && attestedKey.AsSpan().SequenceEqual(recordedKey)));

        return new CardVerification(serial, record, checks);
    }

    private static byte[]? PublicKeyOf(byte[] der)
    {
        try
        {
            using var certificate = X509CertificateLoader.LoadCertificate(der);
            return certificate.PublicKey.EncodedKeyValue.RawData;
        }
        catch (System.Security.Cryptography.CryptographicException)
        {
            return null;
        }
    }

    private static string Thumbprint(byte[] der)
    {
        try
        {
            using var certificate = X509CertificateLoader.LoadCertificate(der);
            return certificate.Thumbprint;
        }
        catch (System.Security.Cryptography.CryptographicException)
        {
            return "not a certificate";
        }
    }
}

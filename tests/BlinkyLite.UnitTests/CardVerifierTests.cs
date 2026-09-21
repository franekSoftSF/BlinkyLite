using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using BlinkyLite.Contracts;
using BlinkyLite.Issuance;
using BlinkyLite.Piv.Attestation;

namespace BlinkyLite.UnitTests;

public sealed class CardVerifierTests
{
    private const uint Serial = 29177301;

    [Fact]
    public void The_card_the_record_describes_passes_every_check()
    {
        using var key = RSA.Create(2048);
        var pki = SyntheticYubico.Build(Serial, attestedKey: key);
        var certificate = CertificateFor(key);

        var result = CardVerifier.Evaluate(Serial, Record(certificate, pki.Leaf.RawData),
            certificate, pki.Leaf, pki.Intermediate, new AttestationVerifier(pki.Roots));

        Assert.True(result.Passed, string.Join("; ", result.Checks.Select(c => $"{c.MessageKey}={c.Passed} {c.Detail}")));
        Assert.Equal(
            [CardVerifier.Known, CardVerifier.CertificateMatches, CardVerifier.AttestationTrusted,
             CardVerifier.KeyIsCertified, CardVerifier.SameKeyAsIssued],
            result.Checks.Select(c => c.MessageKey));
    }

    [Fact]
    public void A_certificate_that_is_not_the_recorded_one_fails_that_check()
    {
        using var key = RSA.Create(2048);
        var pki = SyntheticYubico.Build(Serial, attestedKey: key);

        var result = CardVerifier.Evaluate(Serial, Record(CertificateFor(key), pki.Leaf.RawData),
            CertificateFor(key), pki.Leaf, pki.Intermediate, new AttestationVerifier(pki.Roots));

        Assert.False(Check(result, CardVerifier.CertificateMatches));
        Assert.True(Check(result, CardVerifier.SameKeyAsIssued));
    }

    [Fact]
    public void A_key_generated_again_after_issuance_is_caught_even_with_the_old_certificate_in_place()
    {
        using var issued = RSA.Create(2048);
        using var replaced = RSA.Create(2048);
        var atIssuance = SyntheticYubico.Build(Serial, attestedKey: issued);
        var now = SyntheticYubico.Build(Serial, attestedKey: replaced);
        var certificate = CertificateFor(issued);

        var result = CardVerifier.Evaluate(Serial, Record(certificate, atIssuance.Leaf.RawData),
            certificate, now.Leaf, now.Intermediate, new AttestationVerifier(now.Roots));

        Assert.True(Check(result, CardVerifier.CertificateMatches));
        Assert.False(Check(result, CardVerifier.KeyIsCertified));
        Assert.False(Check(result, CardVerifier.SameKeyAsIssued));
        Assert.False(result.Passed);
    }

    [Fact]
    public void An_attestation_that_does_not_chain_to_the_trusted_roots_fails()
    {
        using var key = RSA.Create(2048);
        var pki = SyntheticYubico.Build(Serial, attestedKey: key);
        var other = SyntheticYubico.Build(Serial);
        var certificate = CertificateFor(key);

        var result = CardVerifier.Evaluate(Serial, Record(certificate, pki.Leaf.RawData),
            certificate, pki.Leaf, pki.Intermediate, new AttestationVerifier(other.Roots));

        Assert.False(Check(result, CardVerifier.AttestationTrusted));
    }

    [Fact]
    public void A_card_the_server_never_issued_is_one_failed_line_and_nothing_else()
    {
        var pki = SyntheticYubico.Build(Serial);

        var result = CardVerifier.Evaluate(Serial, record: null, null, null, null, new AttestationVerifier(pki.Roots));

        Assert.Equal(CardVerifier.Known, Assert.Single(result.Checks).MessageKey);
        Assert.False(result.Passed);
    }

    [Fact]
    public void An_empty_slot_fails_without_throwing()
    {
        using var key = RSA.Create(2048);
        var pki = SyntheticYubico.Build(Serial, attestedKey: key);

        var result = CardVerifier.Evaluate(Serial, Record(CertificateFor(key), pki.Leaf.RawData),
            certificateOnCard: null, attestation: null, intermediate: null, new AttestationVerifier(pki.Roots));

        Assert.All(result.Checks.Skip(1), c => Assert.False(c.Passed));
    }

    private static bool Check(CardVerification result, string key) => result.Checks.Single(c => c.MessageKey == key).Passed;

    private static byte[] CertificateFor(RSA key)
    {
        var request = new CertificateRequest("CN=Jan Kowalski", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        using var certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(1));
        return certificate.RawData;
    }

    private static CardRecord Record(byte[] certificate, byte[] attestation) => new(
        Serial,
        new IssuanceDetails(
            Guid.NewGuid(), Serial, "5.7.1", IssuanceState.Issued, true,
            "Jan Kowalski", @"CORP\jkowalski", "jkowalski@corp.example", "S-1-5-21-1-2-3-42",
            "Karta", "CorpSmartcardLogon", @"SUBCA\Corp CA", 7, "so@corp.example", @"CORP\so", "WS-1",
            null, "Rsa2048", 2, 1, "01", "AB", null, null, null, 0, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow),
        certificate,
        attestation);
}

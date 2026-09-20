using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using BlinkyLite.Contracts;
using BlinkyLite.Piv.Attestation;
using Microsoft.Extensions.DependencyInjection;

namespace BlinkyLite.UnitTests;

/// <summary>
/// The issuance API of 0020: what the server hands out, what it refuses, and
/// the two places where it declines to take the station's word for anything.
/// </summary>
public sealed class IssuanceApiTests
{
    private const long Serial = 29177301;
    private const string TargetSid = "S-1-5-21-100-200-300-7001";

    private static readonly DirectoryUser Target =
        new(@"CORP\jkowalski", "jkowalski@corp.example", TargetSid, "Jan Kowalski", true);

    [Fact]
    public async Task The_profiles_come_from_the_server_not_from_the_station()
    {
        using var factory = new ServerFactory();
        var client = await factory.SignedInAs(Role.SecurityOfficer);

        var profiles = await client.GetFromJsonAsync<List<IssuanceProfile>>("/api/profiles");

        var profile = Assert.Single(profiles!);
        Assert.Equal("Karta", profile.Name);
        Assert.Equal("CorpSmartcardLogon", profile.Template);
        Assert.Equal(@"SUBCA\Corp Issuing CA", profile.CaConfig);
    }

    [Fact]
    public async Task Helpdesk_sees_no_profiles_because_helpdesk_issues_nothing()
    {
        using var factory = new ServerFactory();
        var client = await factory.SignedInAs(Role.Helpdesk);

        var response = await client.GetAsync("/api/profiles");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task A_profile_this_server_does_not_have_is_refused()
    {
        using var factory = new ServerFactory();
        factory.Directory.Users.Add(Target);
        var client = await factory.SignedInAs(Role.Admin);

        var response = await client.PostAsJsonAsync("/api/issuances", Start() with { ProfileName = "Cokolwiek" });

        await response.ShouldBe(HttpStatusCode.BadRequest, ErrorCodes.ProfileUnknown);
        Assert.Empty(factory.Procedures.Reservations);
    }

    [Fact]
    public async Task The_person_is_read_from_the_directory_not_from_the_request()
    {
        using var factory = new ServerFactory();
        factory.Directory.Users.Add(Target);
        var client = await factory.SignedInAs(Role.Admin);

        var response = await client.PostAsJsonAsync("/api/issuances", Start());
        response.EnsureSuccessStatusCode();

        // The request carried a SID and nothing else; everything written down
        // about the person came from AD.
        var reservation = Assert.Single(factory.Procedures.Reservations);
        Assert.Equal(Target.SamAccount, reservation.TargetSam);
        Assert.Equal(Target.Upn, reservation.TargetUpn);
        Assert.Equal(Target.DisplayName, reservation.TargetDisplayName);

        // And the template is the server's, not a name the station could send.
        Assert.Equal("CorpSmartcardLogon", reservation.TemplateName);
        Assert.Equal(@"SUBCA\Corp Issuing CA", reservation.CaConfig);
    }

    [Fact]
    public async Task A_disabled_account_gets_no_key()
    {
        using var factory = new ServerFactory();
        factory.Directory.Users.Add(Target with { Enabled = false });
        var client = await factory.SignedInAs(Role.Admin);

        var response = await client.PostAsJsonAsync("/api/issuances", Start());

        await response.ShouldBe(HttpStatusCode.BadRequest, ErrorCodes.TargetDisabled);
        Assert.Empty(factory.Procedures.Reservations);
    }

    [Fact]
    public async Task A_SID_nobody_has_is_not_found()
    {
        using var factory = new ServerFactory();
        var client = await factory.SignedInAs(Role.Admin);

        var response = await client.PostAsJsonAsync("/api/issuances", Start());

        await response.ShouldBe(HttpStatusCode.NotFound, ErrorCodes.TargetNotFound);
    }

    [Fact]
    public async Task The_secrets_are_sealed_before_the_reservation_is_written()
    {
        using var factory = new ServerFactory();
        factory.Directory.Users.Add(Target);
        var client = await factory.SignedInAs(Role.Admin);

        var response = await client.PostAsJsonAsync("/api/issuances", Start());
        var reservation = (await response.Content.ReadFromJsonAsync<ReservationResponse>())!;

        Assert.Equal(8, reservation.Puk!.Length);
        Assert.All(reservation.Puk, c => Assert.True(char.IsAsciiDigit(c)));
        Assert.Equal(24, Convert.FromBase64String(reservation.ManagementKey).Length);

        // What went to the database is an envelope, not the secret: the PUK
        // must not appear in it, and the management key must not be its bytes.
        var written = Assert.Single(factory.Procedures.Reservations);
        var puk = System.Text.Encoding.ASCII.GetBytes(reservation.Puk);
        Assert.DoesNotContain(Convert.ToHexString(puk), Convert.ToHexString(written.PukEnvelope!));
        Assert.DoesNotContain(
            Convert.ToHexString(Convert.FromBase64String(reservation.ManagementKey)),
            Convert.ToHexString(written.MgmtKeyEnvelope));
    }

    [Fact]
    public async Task An_attestation_that_is_not_Yubicos_is_refused()
    {
        using var key = RSA.Create(2048);
        var pki = SyntheticYubico.Build(serial: (uint)Serial, attestedKey: key);

        // The server here trusts the real Yubico roots, as in production.
        using var factory = new ServerFactory();
        factory.Issuances.Rows[Id] = Reserved();
        var client = await factory.SignedInAs(Role.Admin);

        var response = await client.PostAsJsonAsync($"/api/issuances/{Id}/attestation",
            new AttestationUpload(pki.Leaf.RawData, pki.Intermediate.RawData, Request(key)));

        await response.ShouldBe(HttpStatusCode.BadRequest, ErrorCodes.CardAttestationRefused);
        Assert.Empty(factory.Procedures.Attestations);
    }

    [Fact]
    public async Task An_attestation_of_this_card_and_this_key_is_believed()
    {
        using var key = RSA.Create(2048);
        var pki = SyntheticYubico.Build(serial: (uint)Serial, attestedKey: key);

        using var factory = new ServerFactory { Verifier = new AttestationVerifier(pki.Roots) };
        factory.Issuances.Rows[Id] = Reserved();
        var client = await factory.SignedInAs(Role.Admin);

        var response = await client.PostAsJsonAsync($"/api/issuances/{Id}/attestation",
            new AttestationUpload(pki.Leaf.RawData, pki.Intermediate.RawData, Request(key)));

        response.EnsureSuccessStatusCode();
        var stored = Assert.Single(factory.Procedures.Attestations);
        Assert.Equal(Id, stored.Id);
        Assert.NotEmpty(stored.Record.CsrDer);
    }

    [Fact]
    public async Task A_request_swapped_for_another_key_is_refused()
    {
        // The attack this check exists for: the station keeps the card's
        // attestation - which is genuine - and sends a request for a key it
        // generated itself, so the certificate would come out on a key the
        // card never held.
        using var attested = RSA.Create(2048);
        using var substituted = RSA.Create(2048);
        var pki = SyntheticYubico.Build(serial: (uint)Serial, attestedKey: attested);

        using var factory = new ServerFactory { Verifier = new AttestationVerifier(pki.Roots) };
        factory.Issuances.Rows[Id] = Reserved();
        var client = await factory.SignedInAs(Role.Admin);

        var response = await client.PostAsJsonAsync($"/api/issuances/{Id}/attestation",
            new AttestationUpload(pki.Leaf.RawData, pki.Intermediate.RawData, Request(substituted)));

        await response.ShouldBe(HttpStatusCode.BadRequest, ErrorCodes.CardAttestationRefused);
        Assert.Empty(factory.Procedures.Attestations);
    }

    [Fact]
    public async Task A_certificate_for_another_key_does_not_close_an_issuance()
    {
        using var attested = RSA.Create(2048);
        using var other = RSA.Create(2048);

        using var factory = new ServerFactory();
        factory.Issuances.Rows[Id] = Attested(Request(attested));
        var client = await factory.SignedInAs(Role.Admin);

        var response = await client.PostAsJsonAsync($"/api/issuances/{Id}/complete",
            new CompleteRequest(Certificate(other, Target.Upn)));

        await response.ShouldBe(HttpStatusCode.BadRequest, ErrorCodes.CertificateKeyMismatch);
        Assert.Empty(factory.Procedures.Issued);
    }

    [Fact]
    public async Task A_certificate_for_another_person_does_not_close_an_issuance()
    {
        using var attested = RSA.Create(2048);

        using var factory = new ServerFactory();
        factory.Issuances.Rows[Id] = Attested(Request(attested));
        var client = await factory.SignedInAs(Role.Admin);

        var response = await client.PostAsJsonAsync($"/api/issuances/{Id}/complete",
            new CompleteRequest(Certificate(attested, "operator@corp.example")));

        await response.ShouldBe(HttpStatusCode.BadRequest, ErrorCodes.CertificateTargetMismatch);
        Assert.Empty(factory.Procedures.Issued);
    }

    [Fact]
    public async Task A_certificate_for_the_attested_key_and_the_right_person_closes_it()
    {
        using var attested = RSA.Create(2048);

        using var factory = new ServerFactory();
        factory.Issuances.Rows[Id] = Attested(Request(attested));
        var client = await factory.SignedInAs(Role.Admin);

        var response = await client.PostAsJsonAsync($"/api/issuances/{Id}/complete",
            new CompleteRequest(Certificate(attested, Target.Upn)));

        response.EnsureSuccessStatusCode();
        var issued = Assert.Single(factory.Procedures.Issued);
        Assert.Equal(Id, issued.Id);
        Assert.NotEmpty(issued.Certificate.Thumbprint);
    }

    private static readonly Guid Id = Guid.Parse("0199a0f0-0000-7000-8000-000000000001");

    private static StartIssuanceRequest Start() =>
        new(Serial, "5.7.1", HasPuk: true, "Aes192", "Karta", TargetSid, "DPCLIENT02");

    /// <summary>A PKCS#10 the way the card makes one: signed by the key it is about.</summary>
    private static byte[] Request(RSA key) =>
        new CertificateRequest("CN=Jan Kowalski", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1)
            .CreateSigningRequest();

    /// <summary>What a CA would hand back: a certificate on that key, naming that person.</summary>
    private static byte[] Certificate(RSA key, string upn)
    {
        var request = new CertificateRequest(
            "CN=Jan Kowalski", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);

        var names = new SubjectAlternativeNameBuilder();
        names.AddUserPrincipalName(upn);
        request.CertificateExtensions.Add(names.Build());

        using var certificate = request.CreateSelfSigned(
            DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddYears(1));

        return certificate.RawData;
    }

    private static BlinkyLite.Server.Data.Issuance Reserved() =>
        ReadModelFactory.Issuance(Id, Serial, Target.Upn, csr: null);

    private static BlinkyLite.Server.Data.Issuance Attested(byte[] csr) =>
        ReadModelFactory.Issuance(Id, Serial, Target.Upn, csr);
}

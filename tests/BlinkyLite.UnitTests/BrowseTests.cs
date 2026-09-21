using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using BlinkyLite.Contracts;
using BlinkyLite.Server.Data;
using BlinkyLite.Server.Secrets;
using ServerIssuance = BlinkyLite.Server.Data.Issuance;

namespace BlinkyLite.UnitTests;

/// <summary>
/// 0030: who sees what. The split is enforced by the API, so it is tested on
/// the API - a console that hid a field would pass a UI test and leak here.
/// </summary>
public sealed class BrowseTests : IClassFixture<ServerFactory>
{
    private const string Puk = "48271930";
    private static readonly byte[] ManagementKey = Enumerable.Range(1, 24).Select(i => (byte)i).ToArray();

    private readonly ServerFactory server;
    private readonly long serial;
    private readonly Guid issuanceId = Guid.NewGuid();

    public BrowseTests(ServerFactory server)
    {
        this.server = server;
        serial = Random.Shared.NextInt64(10_000_000, 99_999_999);

        server.Issuances.Rows[issuanceId] = ReadModelFactory.Row<ServerIssuance>(
            (nameof(ServerIssuance.Id), issuanceId),
            (nameof(ServerIssuance.CardSerial), serial),
            (nameof(ServerIssuance.State), IssuanceState.Issued),
            (nameof(ServerIssuance.TargetDisplayName), "Jan Kowalski"),
            (nameof(ServerIssuance.TargetSam), @"CORP\jkowalski"),
            (nameof(ServerIssuance.TargetUpn), "jkowalski@corp.example"),
            (nameof(ServerIssuance.OperatorUpn), "so@corp.example"),
            (nameof(ServerIssuance.Workstation), "WS-042"),
            (nameof(ServerIssuance.CertificateDer), new byte[] { 0x30, 0x01 }),
            (nameof(ServerIssuance.CreatedAt), DateTime.UtcNow));
        server.Issuances.Cards[serial] = ReadModelFactory.Row<Card>(
            (nameof(Card.Serial), serial),
            (nameof(Card.Firmware), "5.7.1"),
            (nameof(Card.CurrentIssuanceId), (Guid?)issuanceId));
        server.Issuances.Secrets[issuanceId] = ReadModelFactory.Row<CardSecret>(
            (nameof(CardSecret.IssuanceId), issuanceId),
            (nameof(CardSecret.CardSerial), serial),
            (nameof(CardSecret.MgmtKeyAlgorithm), (short)0x0A),
            (nameof(CardSecret.PukDisclosedCount), 2));

        // Sealed with the factory's KEK, exactly as the reservation would have.
        var envelopes = new SecretEnvelopes(new KekOptions
        {
            CurrentKekVersion = 1,
            Keks = { ["1"] = Convert.ToBase64String(Enumerable.Range(0, 32).Select(i => (byte)i).ToArray()) },
        });
        server.Procedures.Envelopes[(serial, SecretKind.Puk)] = new SecretEnvelope(issuanceId,
            envelopes.Seal(Encoding.ASCII.GetBytes(Puk), new EnvelopeBinding(EnvelopeKind.Puk, serial, issuanceId)), 1);
        server.Procedures.Envelopes[(serial, SecretKind.ManagementKey)] = new SecretEnvelope(issuanceId,
            envelopes.Seal(ManagementKey, new EnvelopeBinding(EnvelopeKind.ManagementKey, serial, issuanceId)), 1);
    }

    [Fact]
    public async Task Helpdesk_gets_the_list_and_nothing_but_who_which_card_when_and_state()
    {
        using var helpdesk = await server.SignedInAs(Role.Helpdesk);

        var json = await helpdesk.GetStringAsync("/api/issuances?q=Kowal");
        using var document = JsonDocument.Parse(json);
        var row = document.RootElement.GetProperty("items").EnumerateArray()
            .Single(r => r.GetProperty("cardSerial").GetInt64() == serial);

        Assert.Equal(
            ["cardSerial", "createdAt", "id", "state", "targetDisplayName", "targetSam"],
            row.EnumerateObject().Select(p => p.Name).Order());
        Assert.DoesNotContain(Puk, json);
    }

    [Theory]
    [InlineData(Role.Helpdesk, HttpStatusCode.Forbidden)]
    [InlineData(Role.SecurityOfficer, HttpStatusCode.OK)]
    [InlineData(Role.Admin, HttpStatusCode.OK)]
    public async Task Details_and_the_card_record_are_for_those_who_issue(Role role, HttpStatusCode expected)
    {
        using var client = await server.SignedInAs(role);

        Assert.Equal(expected, (await client.GetAsync($"/api/issuances/{issuanceId}")).StatusCode);
        Assert.Equal(expected, (await client.GetAsync($"/api/cards/{serial}")).StatusCode);
    }

    [Fact]
    public async Task Details_say_whether_the_issuance_is_the_cards_current_one_and_how_often_its_PUK_was_shown()
    {
        using var officer = await server.SignedInAs(Role.SecurityOfficer);

        var details = await officer.GetFromJsonAsync<IssuanceDetails>($"/api/issuances/{issuanceId}");

        Assert.True(details!.IsCurrent);
        Assert.Equal(2, details.PukDisclosedCount);
        Assert.Equal("5.7.1", details.Firmware);
        Assert.Equal("WS-042", details.Workstation);
    }

    [Fact]
    public async Task Helpdesk_gets_one_PUK_with_a_reason_and_the_reason_goes_to_the_disclosure()
    {
        using var helpdesk = await server.SignedInAs(Role.Helpdesk);

        var response = await helpdesk.PostAsJsonAsync($"/api/cards/{serial}/puk", new RevealRequest("INC-4711 PIN blocked"));
        var revealed = await response.Content.ReadFromJsonAsync<RevealedPuk>();

        Assert.Equal(Puk, revealed!.Puk);
        var disclosure = server.Procedures.Disclosures.Last(d => d.Serial == serial);
        Assert.Equal((SecretKind.Puk, "INC-4711 PIN blocked"), (disclosure.Kind, disclosure.Reason));
        Assert.Equal([Role.Helpdesk], disclosure.Actor.Roles);
    }

    [Fact]
    public async Task A_PUK_without_a_reason_is_refused_before_anything_is_opened()
    {
        using var helpdesk = await server.SignedInAs(Role.Helpdesk);

        var response = await helpdesk.PostAsJsonAsync($"/api/cards/{serial}/puk", new RevealRequest("abc"));

        await response.ShouldBe(HttpStatusCode.BadRequest, ErrorCodes.ReasonRequired);
    }

    [Fact]
    public async Task A_card_the_server_never_issued_has_no_PUK_to_show()
    {
        using var helpdesk = await server.SignedInAs(Role.Helpdesk);

        var response = await helpdesk.PostAsJsonAsync("/api/cards/1/puk", new RevealRequest("INC-1 no such card"));

        await response.ShouldBe(HttpStatusCode.NotFound, ErrorCodes.NotFound);
    }

    [Theory]
    [InlineData(Role.Helpdesk)]
    [InlineData(Role.SecurityOfficer)]
    public async Task Only_Admin_gets_the_management_key(Role role)
    {
        using var client = await server.SignedInAs(role);

        var response = await client.PostAsJsonAsync($"/api/cards/{serial}/management-key", new RevealRequest("INC-9 card reissue"));

        await response.ShouldBe(HttpStatusCode.Forbidden, ErrorCodes.Forbidden);
    }

    [Fact]
    public async Task Admin_gets_the_management_key_in_hex_with_its_algorithm()
    {
        using var admin = await server.SignedInAs(Role.Admin);

        var response = await admin.PostAsJsonAsync($"/api/cards/{serial}/management-key", new RevealRequest("INC-9 card reissue"));
        var revealed = await response.Content.ReadFromJsonAsync<RevealedManagementKey>();

        Assert.Equal(Convert.ToHexString(ManagementKey), revealed!.Key);
        Assert.Equal("Aes192", revealed.Algorithm);
    }

    [Theory]
    [InlineData(Role.Helpdesk)]
    [InlineData(Role.SecurityOfficer)]
    public async Task The_audit_is_Admins_alone(Role role)
    {
        using var client = await server.SignedInAs(role);

        await (await client.GetAsync("/api/audit")).ShouldBe(HttpStatusCode.Forbidden, ErrorCodes.Forbidden);
    }

    [Fact]
    public async Task Admin_reads_the_audit_of_one_card_newest_first()
    {
        server.Issuances.AuditRows.Add(ReadModelFactory.Row<AuditEvent>(
            (nameof(AuditEvent.Id), 900L), (nameof(AuditEvent.Action), "issuance.issued"),
            (nameof(AuditEvent.ActorUpn), "so@corp.example"), (nameof(AuditEvent.ActorRoles), "SecurityOfficer"),
            (nameof(AuditEvent.CardSerial), (long?)serial), (nameof(AuditEvent.At), DateTime.UtcNow)));
        server.Issuances.AuditRows.Add(ReadModelFactory.Row<AuditEvent>(
            (nameof(AuditEvent.Id), 901L), (nameof(AuditEvent.Action), "puk.disclosed"),
            (nameof(AuditEvent.ActorUpn), "hd@corp.example"), (nameof(AuditEvent.ActorRoles), "Helpdesk"),
            (nameof(AuditEvent.CardSerial), (long?)serial), (nameof(AuditEvent.At), DateTime.UtcNow)));
        using var admin = await server.SignedInAs(Role.Admin);

        var page = await admin.GetFromJsonAsync<Page<AuditEntry>>($"/api/audit?card={serial}");

        Assert.Equal(["puk.disclosed", "issuance.issued"], page!.Items.Select(a => a.Action));
        Assert.Equal(["Helpdesk"], page.Items[0].ActorRoles);
    }

    [Fact]
    public async Task A_page_size_is_clamped_rather_than_refused()
    {
        using var helpdesk = await server.SignedInAs(Role.Helpdesk);

        var page = await helpdesk.GetFromJsonAsync<Page<IssuanceListItem>>("/api/issuances?pageSize=100000&page=0");

        Assert.Equal(100, page!.PageSize);
        Assert.Equal(1, page.PageNumber);
    }
}

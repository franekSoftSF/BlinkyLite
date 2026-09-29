using System.Net;
using System.Net.Http.Json;
using System.Text;
using BlinkyLite.Contracts;
using BlinkyLite.Server.Data;
using BlinkyLite.Server.Secrets;
using ServerIssuance = BlinkyLite.Server.Data.Issuance;

namespace BlinkyLite.UnitTests;

/// <summary>
/// 0058: a key that left service. The point of the feature is what stops
/// working afterwards, so that is what is tested - the PUK, the management key
/// and the remote unblock all have to refuse.
/// </summary>
public sealed class WithdrawTests : IClassFixture<ServerFactory>
{
    private const string Puk = "77412903";

    private readonly ServerFactory server;
    private readonly long serial;

    public WithdrawTests(ServerFactory server)
    {
        this.server = server;
        serial = Random.Shared.NextInt64(10_000_000, 99_999_999);
        var issuanceId = Guid.NewGuid();

        server.Issuances.Rows[issuanceId] = ReadModelFactory.Row<ServerIssuance>(
            (nameof(ServerIssuance.Id), issuanceId),
            (nameof(ServerIssuance.CardSerial), serial),
            (nameof(ServerIssuance.State), IssuanceState.Issued),
            (nameof(ServerIssuance.TargetDisplayName), "Piotr Zielinski"),
            (nameof(ServerIssuance.TargetSam), @"CORP\pzielinski"),
            (nameof(ServerIssuance.CreatedAt), DateTime.UtcNow));
        server.Issuances.Cards[serial] = ReadModelFactory.Row<Card>(
            (nameof(Card.Serial), serial),
            (nameof(Card.Firmware), "5.7.1"),
            (nameof(Card.CurrentIssuanceId), (Guid?)issuanceId));

        var envelopes = new SecretEnvelopes(new KekOptions
        {
            CurrentKekVersion = 1,
            Keks = { ["1"] = Convert.ToBase64String(Enumerable.Range(0, 32).Select(i => (byte)i).ToArray()) },
        });
        server.Procedures.Envelopes[(serial, SecretKind.Puk)] = new SecretEnvelope(issuanceId,
            envelopes.Seal(Encoding.ASCII.GetBytes(Puk), new EnvelopeBinding(EnvelopeKind.Puk, serial, issuanceId)), 1);
    }

    [Theory]
    [InlineData(Role.Helpdesk, HttpStatusCode.Forbidden)]
    [InlineData(Role.SecurityOfficer, HttpStatusCode.NoContent)]
    [InlineData(Role.Admin, HttpStatusCode.NoContent)]
    public async Task Whoever_may_put_a_key_into_service_may_take_it_out(Role role, HttpStatusCode expected)
    {
        using var client = await server.SignedInAs(role);

        var response = await client.PostAsJsonAsync($"/api/cards/{serial}/withdraw",
            new WithdrawRequest("klucz skasowany przy zwrocie"));

        Assert.Equal(expected, response.StatusCode);
    }

    [Fact]
    public async Task A_withdrawal_without_a_reason_is_refused()
    {
        using var officer = await server.SignedInAs(Role.SecurityOfficer);

        var response = await officer.PostAsJsonAsync($"/api/cards/{serial}/withdraw", new WithdrawRequest("ok"));

        await response.ShouldBe(HttpStatusCode.BadRequest, ErrorCodes.ReasonRequired);
    }

    [Fact]
    public async Task After_it_the_PUK_cannot_be_revealed_any_more()
    {
        using var officer = await server.SignedInAs(Role.SecurityOfficer);
        using var helpdesk = await server.SignedInAs(Role.Helpdesk);

        var before = await helpdesk.PostAsJsonAsync($"/api/cards/{serial}/puk", new RevealRequest("zgloszenie INC-9"));
        Assert.Equal(HttpStatusCode.OK, before.StatusCode);

        await Withdraw(officer, "klucz skasowany recznie po zwrocie");

        var after = await helpdesk.PostAsJsonAsync($"/api/cards/{serial}/puk", new RevealRequest("zgloszenie INC-9"));
        await after.ShouldBe(HttpStatusCode.NotFound, ErrorCodes.NotFound);
    }

    /// <summary>
    /// The reason the feature exists: a wiped card whose record still claims a
    /// PUK would hand that PUK to a workstation and burn the card's attempts.
    /// </summary>
    [Fact]
    public async Task After_it_a_remote_unblock_is_refused_before_anybody_is_asked()
    {
        using var officer = await server.SignedInAs(Role.SecurityOfficer);
        using var station = server.CreateClient();

        await Withdraw(officer, "klucz zgubiony, zgloszenie INC-12");

        var response = await station.PostAsJsonAsync("/api/unlock/start", new UnlockRequest(serial, "WS-042"));

        await response.ShouldBe(HttpStatusCode.NotFound, ErrorCodes.NotFound);
    }

    [Fact]
    public async Task A_request_already_waiting_stops_being_approvable()
    {
        using var officer = await server.SignedInAs(Role.SecurityOfficer);
        using var station = server.CreateClient();

        var asked = await station.PostAsJsonAsync("/api/unlock/start", new UnlockRequest(serial, "WS-042"));
        var ticket = (await asked.Content.ReadFromJsonAsync<UnlockTicket>())!;

        await Withdraw(officer, "klucz skasowany w trakcie rozmowy");

        var state = await station.PostAsJsonAsync($"/api/unlock/{ticket.RequestId}/state", new UnlockSecret(ticket.Secret));
        var answer = (await state.Content.ReadFromJsonAsync<UnlockState>())!;

        Assert.Equal(UnlockStates.Expired, answer.State);
        Assert.Null(answer.Puk);
    }

    [Fact]
    public async Task Withdrawing_twice_is_refused_so_it_reads_like_what_happened()
    {
        using var officer = await server.SignedInAs(Role.SecurityOfficer);
        await Withdraw(officer, "klucz oddany i skasowany");

        var again = await officer.PostAsJsonAsync($"/api/cards/{serial}/withdraw",
            new WithdrawRequest("klucz oddany i skasowany"));

        await again.ShouldBe(HttpStatusCode.Conflict, ErrorCodes.CardWithdrawn);
    }

    [Fact]
    public async Task The_reason_is_recorded_with_the_operator_who_gave_it()
    {
        using var officer = await server.SignedInAs(Role.SecurityOfficer);

        await Withdraw(officer, "klucz zgubiony przez uzytkownika, INC-31");

        var withdrawal = Assert.Single(server.Procedures.Withdrawals, w => w.Serial == serial);
        Assert.Equal("klucz zgubiony przez uzytkownika, INC-31", withdrawal.Reason);
        Assert.Contains(Role.SecurityOfficer, withdrawal.Actor.Roles);
    }

    private async Task Withdraw(HttpClient client, string reason)
    {
        var response = await client.PostAsJsonAsync($"/api/cards/{serial}/withdraw", new WithdrawRequest(reason));
        response.EnsureSuccessStatusCode();
    }
}

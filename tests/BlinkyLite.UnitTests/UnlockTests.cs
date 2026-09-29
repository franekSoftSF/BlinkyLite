using System.Net;
using System.Net.Http.Json;
using System.Text;
using BlinkyLite.Contracts;
using BlinkyLite.Server.Data;
using BlinkyLite.Server.Secrets;
using ServerIssuance = BlinkyLite.Server.Data.Issuance;

namespace BlinkyLite.UnitTests;

/// <summary>
/// 0057: the remote unblock. Three of these routes are anonymous, so what they
/// refuse matters more than what they return - the tests here are mostly about
/// the PUK staying where it is.
/// </summary>
public sealed class UnlockTests : IClassFixture<ServerFactory>
{
    private const string Puk = "31908472";

    private readonly ServerFactory server;
    private readonly long serial;

    public UnlockTests(ServerFactory server)
    {
        this.server = server;
        serial = Random.Shared.NextInt64(10_000_000, 99_999_999);
        var issuanceId = Guid.NewGuid();

        server.Issuances.Rows[issuanceId] = ReadModelFactory.Row<ServerIssuance>(
            (nameof(ServerIssuance.Id), issuanceId),
            (nameof(ServerIssuance.CardSerial), serial),
            (nameof(ServerIssuance.State), IssuanceState.Issued),
            (nameof(ServerIssuance.TargetDisplayName), "Anna Nowak"),
            (nameof(ServerIssuance.TargetSam), @"CORP\anowak"),
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

    [Fact]
    public async Task A_workstation_with_nobody_signed_in_may_ask_and_gets_a_code_to_read_out()
    {
        using var station = server.CreateClient();

        var ticket = await Ask(station);

        Assert.NotEqual(Guid.Empty, ticket.RequestId);
        Assert.Matches("^[A-Z2-9]{3}-[A-Z2-9]{3}$", ticket.Code);

        // The alphabet leaves out the characters people mishear when they spell
        // a code down a telephone line.
        Assert.DoesNotContain(ticket.Code, c => "OIL01".Contains(c));
        Assert.Equal("unlock.requested", server.Procedures.UnlockAudits.Single(a => a.Request == ticket.RequestId).Action);
    }

    [Fact]
    public async Task Before_anybody_approves_it_the_answer_is_the_state_and_no_PUK()
    {
        using var station = server.CreateClient();
        var ticket = await Ask(station);

        var state = await State(station, ticket);

        Assert.Equal(UnlockStates.Pending, state.State);
        Assert.Null(state.Puk);
        Assert.DoesNotContain(server.Procedures.Disclosures, d => d.Serial == serial);
    }

    [Fact]
    public async Task After_an_approval_the_PUK_is_handed_over_once_and_the_disclosure_is_on_record()
    {
        using var station = server.CreateClient();
        using var helpdesk = await server.SignedInAs(Role.Helpdesk);
        var ticket = await Ask(station);

        await Decide(helpdesk, ticket, "approve", "Anna dzwonila z numeru wewnetrznego 412");
        var first = await State(station, ticket);
        var second = await State(station, ticket);

        Assert.Equal(Puk, first.Puk);
        Assert.Equal(UnlockStates.Delivered, first.State);

        // The second call finds the request already delivered, which is the
        // whole point of flipping the state in the same statement.
        Assert.Null(second.Puk);
        Assert.Equal(UnlockStates.Delivered, second.State);

        var disclosure = Assert.Single(server.Procedures.Disclosures, d => d.Serial == serial);
        Assert.Equal(SecretKind.Puk, disclosure.Kind);
        Assert.Contains("412", disclosure.Reason);
    }

    [Fact]
    public async Task The_code_is_not_enough_a_request_asked_for_with_the_wrong_secret_looks_unknown()
    {
        using var station = server.CreateClient();
        using var helpdesk = await server.SignedInAs(Role.Helpdesk);
        var ticket = await Ask(station);
        await Decide(helpdesk, ticket, "approve", "sprawdzone przy telefonie");

        // Somebody who overheard the code, and the approval, but never had the
        // secret the workstation generated.
        var response = await station.PostAsJsonAsync(
            $"/api/unlock/{ticket.RequestId}/state", new UnlockSecret("not-the-secret"));

        await response.ShouldBe(HttpStatusCode.NotFound, ErrorCodes.NotFound);
        Assert.DoesNotContain(server.Procedures.Disclosures, d => d.Serial == serial);
    }

    [Fact]
    public async Task A_card_this_server_never_issued_has_no_PUK_to_hand_out()
    {
        using var station = server.CreateClient();

        var response = await station.PostAsJsonAsync("/api/unlock/start", new UnlockRequest(4_242_424, "WS-099"));

        await response.ShouldBe(HttpStatusCode.NotFound, ErrorCodes.NotFound);
    }

    [Fact]
    public async Task The_waiting_list_shows_who_is_calling_and_never_the_PUK()
    {
        using var station = server.CreateClient();
        using var helpdesk = await server.SignedInAs(Role.Helpdesk);
        var ticket = await Ask(station);

        var json = await helpdesk.GetStringAsync("/api/unlock/requests");
        var rows = (await helpdesk.GetFromJsonAsync<List<UnlockWaiting>>("/api/unlock/requests"))!;
        var row = rows.Single(r => r.Id == ticket.RequestId);

        Assert.Equal(ticket.Code, row.Code);
        Assert.Equal(serial, row.CardSerial);
        Assert.Equal("WS-042", row.Workstation);
        Assert.DoesNotContain(Puk, json);
        Assert.DoesNotContain(ticket.Secret, json);
    }

    [Fact]
    public async Task Deciding_is_for_the_roles_that_may_reveal_a_PUK_and_nobody_else()
    {
        using var station = server.CreateClient();
        var ticket = await Ask(station);

        Assert.Equal(HttpStatusCode.Unauthorized, (await station.GetAsync("/api/unlock/requests")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await station.PostAsJsonAsync(
            $"/api/unlock/requests/{ticket.RequestId}/approve", new UnlockDecision("sam siebie"))).StatusCode);
    }

    [Fact]
    public async Task An_approval_without_a_reason_is_refused_because_nothing_explains_it_later()
    {
        using var station = server.CreateClient();
        using var helpdesk = await server.SignedInAs(Role.Helpdesk);
        var ticket = await Ask(station);

        var response = await helpdesk.PostAsJsonAsync(
            $"/api/unlock/requests/{ticket.RequestId}/approve", new UnlockDecision("ok"));

        await response.ShouldBe(HttpStatusCode.BadRequest, ErrorCodes.ReasonRequired);
    }

    [Fact]
    public async Task A_refusal_ends_it_and_the_workstation_is_told_so()
    {
        using var station = server.CreateClient();
        using var helpdesk = await server.SignedInAs(Role.Helpdesk);
        var ticket = await Ask(station);

        await Decide(helpdesk, ticket, "refuse", "nie rozpoznaje osoby dzwoniacej");
        var state = await State(station, ticket);

        Assert.Equal(UnlockStates.Refused, state.State);
        Assert.Null(state.Puk);
        Assert.Equal("unlock.refused", server.Procedures.UnlockAudits.Last(a => a.Request == ticket.RequestId).Action);
    }

    [Fact]
    public async Task Deciding_twice_is_refused_so_a_second_operator_cannot_reopen_it()
    {
        using var station = server.CreateClient();
        using var helpdesk = await server.SignedInAs(Role.Helpdesk);
        var ticket = await Ask(station);
        await Decide(helpdesk, ticket, "refuse", "pomylka, oddzwoni");

        var response = await helpdesk.PostAsJsonAsync(
            $"/api/unlock/requests/{ticket.RequestId}/approve", new UnlockDecision("jednak to ona"));

        await response.ShouldBe(HttpStatusCode.Conflict, ErrorCodes.UnlockInvalidState);
    }

    [Fact]
    public async Task A_second_call_about_the_same_key_replaces_the_first_so_one_code_is_live()
    {
        using var station = server.CreateClient();
        using var helpdesk = await server.SignedInAs(Role.Helpdesk);
        var first = await Ask(station);
        var second = await Ask(station);

        var rows = (await helpdesk.GetFromJsonAsync<List<UnlockWaiting>>("/api/unlock/requests"))!;

        Assert.DoesNotContain(rows, r => r.Id == first.RequestId);
        Assert.Contains(rows, r => r.Id == second.RequestId);
        Assert.Equal(UnlockStates.Expired, (await State(station, first)).State);
    }

    [Fact]
    public async Task What_the_key_said_is_written_down_too_so_the_trail_does_not_end_at_handed_out()
    {
        using var station = server.CreateClient();
        using var helpdesk = await server.SignedInAs(Role.Helpdesk);
        var ticket = await Ask(station);
        await Decide(helpdesk, ticket, "approve", "rozpoznana przy telefonie");
        await State(station, ticket);

        var response = await station.PostAsJsonAsync($"/api/unlock/{ticket.RequestId}/result",
            new UnlockOutcome(ticket.Secret, true, null));

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Equal("unlock.completed", server.Procedures.UnlockAudits.Last(a => a.Request == ticket.RequestId).Action);
    }

    [Fact]
    public async Task A_result_for_a_request_that_was_never_delivered_is_refused()
    {
        using var station = server.CreateClient();
        var ticket = await Ask(station);

        var response = await station.PostAsJsonAsync($"/api/unlock/{ticket.RequestId}/result",
            new UnlockOutcome(ticket.Secret, true, null));

        await response.ShouldBe(HttpStatusCode.Conflict, ErrorCodes.UnlockInvalidState);
    }

    private async Task<UnlockTicket> Ask(HttpClient station)
    {
        var response = await station.PostAsJsonAsync("/api/unlock/start", new UnlockRequest(serial, "WS-042"));
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<UnlockTicket>())!;
    }

    private static async Task<UnlockState> State(HttpClient station, UnlockTicket ticket)
    {
        var response = await station.PostAsJsonAsync(
            $"/api/unlock/{ticket.RequestId}/state", new UnlockSecret(ticket.Secret));
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<UnlockState>())!;
    }

    private static async Task Decide(HttpClient console, UnlockTicket ticket, string decision, string reason)
    {
        var response = await console.PostAsJsonAsync(
            $"/api/unlock/requests/{ticket.RequestId}/{decision}", new UnlockDecision(reason));
        response.EnsureSuccessStatusCode();
    }
}

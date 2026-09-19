using BlinkyLite.Contracts;
using BlinkyLite.Server.Data;
using Npgsql;

namespace BlinkyLite.DbTests;

[Collection(DatabaseCollection.Name)]
public sealed class IssuanceFunctionTests(DatabaseFixture db)
{
    private readonly Scenario scenario = new(db);

    [DbFact]
    public async Task A_full_issuance_moves_through_every_state_and_leaves_one_audit_event_per_step()
    {
        var serial = db.NewSerial();
        var procedures = scenario.Procedures;

        var id = await procedures.ReserveIssuanceAsync(Scenario.Reservation(serial), Scenario.Officer);
        Assert.Equal("Reserved", await scenario.StateOf(id));

        await procedures.MarkCustomisedAsync(id, Scenario.Officer);
        await procedures.MarkAttestedAsync(id, Scenario.Attestation(), Scenario.Officer);
        await procedures.MarkSubmittedAsync(id, 4711, "AB12CD34", Scenario.Officer);
        await procedures.MarkPendingAsync(id, Scenario.Officer);
        await procedures.MarkIssuedAsync(id, Scenario.Certificate(), Scenario.Officer);

        Assert.Equal("Issued", await scenario.StateOf(id));
        Assert.Equal(
            ["issuance.reserved", "issuance.customised", "issuance.attested", "issuance.submitted",
             "issuance.pending", "issuance.issued"],
            await scenario.AuditActions(id));
        Assert.Equal(id, await scenario.Scalar("select current_issuance_id from blinkylite.cards where serial = $1", serial));
        Assert.Equal("Active", await scenario.Scalar("select state from blinkylite.card_secrets where issuance_id = $1", id));
        Assert.Equal(4711, await scenario.Scalar("select ca_request_id from blinkylite.issuances where id = $1", id));
    }

    public static TheoryData<string, IssuanceState> ForbiddenTransitions()
    {
        var allowed = new Dictionary<string, IssuanceState[]>
        {
            ["customised"] = [IssuanceState.Reserved],
            ["attested"] = [IssuanceState.Customised, IssuanceState.Attested],
            ["submitted"] = [IssuanceState.Attested],
            ["pending"] = [IssuanceState.Attested],
            ["issued"] = [IssuanceState.Attested, IssuanceState.PendingCa],
            ["failed"] = [IssuanceState.Reserved, IssuanceState.Customised, IssuanceState.Attested, IssuanceState.PendingCa],
        };

        var data = new TheoryData<string, IssuanceState>();
        foreach (var (function, from) in allowed)
        {
            foreach (var state in Enum.GetValues<IssuanceState>().Except(from))
            {
                data.Add(function, state);
            }
        }

        return data;
    }

    [DbTheory]
    [MemberData(nameof(ForbiddenTransitions))]
    public async Task A_transition_from_a_state_it_does_not_start_from_is_refused_with_BL001(string function, IssuanceState state)
    {
        var (_, id) = await scenario.IssuanceIn(state);
        var auditBefore = await scenario.AuditActions(id);

        var error = await Assert.ThrowsAsync<DatabaseRuleException>(() => Call(function, id));

        Assert.Equal("BL001", error.SqlState);
        Assert.Equal("error.issuance.invalid-state", error.MessageKey);
        Assert.Equal(state.ToString(), await scenario.StateOf(id));
        Assert.Equal(auditBefore, await scenario.AuditActions(id));
    }

    [DbFact]
    public async Task Pending_needs_a_submitted_request()
    {
        var (_, id) = await scenario.IssuanceIn(IssuanceState.Attested);

        var error = await Assert.ThrowsAsync<DatabaseRuleException>(() => scenario.Procedures.MarkPendingAsync(id, Scenario.Officer));

        Assert.Equal("BL001", error.SqlState);
    }

    [DbFact]
    public async Task An_unknown_issuance_is_BL002()
    {
        var error = await Assert.ThrowsAsync<DatabaseRuleException>(
            () => scenario.Procedures.MarkCustomisedAsync(Guid.NewGuid(), Scenario.Officer));

        Assert.Equal("BL002", error.SqlState);
        Assert.Equal("error.not-found", error.MessageKey);
    }

    [DbFact]
    public async Task Helpdesk_cannot_issue()
    {
        var error = await Assert.ThrowsAsync<DatabaseRuleException>(
            () => scenario.Procedures.ReserveIssuanceAsync(Scenario.Reservation(db.NewSerial()), Scenario.Helpdesk));

        Assert.Equal("BL004", error.SqlState);
    }

    [DbFact]
    public async Task A_card_with_an_issuance_in_flight_cannot_be_reserved_again()
    {
        var (serial, _) = await scenario.IssuanceIn(IssuanceState.Attested);

        var error = await Assert.ThrowsAsync<DatabaseRuleException>(
            () => scenario.Procedures.ReserveIssuanceAsync(Scenario.Reservation(serial), Scenario.Officer));

        Assert.Equal("BL003", error.SqlState);
        Assert.Equal("error.card.reserved-elsewhere", error.MessageKey);
    }

    [DbFact]
    public async Task A_second_issuance_supersedes_the_first_and_retires_its_management_key()
    {
        var serial = db.NewSerial();
        var first = await scenario.IssuanceIn(IssuanceState.Issued, serial);
        var second = await scenario.IssuanceIn(IssuanceState.Issued, serial);

        Assert.Equal("Superseded", await scenario.StateOf(first));
        Assert.Equal("Issued", await scenario.StateOf(second));
        Assert.Equal("Retired", await scenario.Scalar("select state from blinkylite.card_secrets where issuance_id = $1", first));
        Assert.Equal("Active", await scenario.Scalar("select state from blinkylite.card_secrets where issuance_id = $1", second));
        Assert.Equal(second, await scenario.Scalar("select current_issuance_id from blinkylite.cards where serial = $1", serial));
        Assert.Contains("issuance.superseded", await scenario.AuditActions(first));
    }

    [DbFact]
    public async Task A_failed_reservation_keeps_its_envelopes()
    {
        var (serial, id) = await scenario.IssuanceIn(IssuanceState.Failed);

        Assert.Equal("Failed", await scenario.StateOf(id));
        Assert.Equal("Reserved", await scenario.Scalar("select state from blinkylite.card_secrets where issuance_id = $1", id));
        Assert.Equal(1L, await scenario.Scalar("select count(*) from blinkylite.card_secrets where card_serial = $1", serial));
        Assert.Equal("test failure", await scenario.Scalar("select error from blinkylite.issuances where id = $1", id));
    }

    [DbFact]
    public async Task When_the_audit_insert_fails_the_change_is_rolled_back_with_it()
    {
        var (_, id) = await scenario.IssuanceIn(IssuanceState.Reserved);
        var nobody = Scenario.Officer with { Upn = "" };

        // actor_upn '' violates the CHECK on audit_events - the last thing the
        // function does, after it has already changed the issuance and secrets.
        var error = await Assert.ThrowsAsync<PostgresException>(() => scenario.Procedures.MarkCustomisedAsync(id, nobody));

        Assert.Equal(PostgresErrorCodes.CheckViolation, error.SqlState);
        Assert.Equal("Reserved", await scenario.StateOf(id));
        Assert.Equal("Reserved", await scenario.Scalar("select state from blinkylite.card_secrets where issuance_id = $1", id));
        Assert.Equal(["issuance.reserved"], await scenario.AuditActions(id));
    }

    [DbFact]
    public async Task A_requester_name_the_CA_would_misread_is_refused()
    {
        var reservation = Scenario.Reservation(db.NewSerial()) with { TargetSam = @"CORP\j&kowalski" };

        var error = await Assert.ThrowsAsync<PostgresException>(
            () => scenario.Procedures.ReserveIssuanceAsync(reservation, Scenario.Officer));

        Assert.Equal(PostgresErrorCodes.CheckViolation, error.SqlState);
    }

    [DbFact]
    public async Task A_PUK_envelope_is_required_exactly_when_the_card_has_a_PUK()
    {
        var reservation = Scenario.Reservation(db.NewSerial()) with { PukEnvelope = null };

        var error = await Assert.ThrowsAsync<PostgresException>(
            () => scenario.Procedures.ReserveIssuanceAsync(reservation, Scenario.Officer));

        Assert.Equal(PostgresErrorCodes.InvalidParameterValue, error.SqlState);
    }

    [DbFact]
    public async Task A_local_time_never_reaches_the_database()
    {
        var (_, id) = await scenario.IssuanceIn(IssuanceState.Attested);
        var local = Scenario.Certificate() with { NotAfter = DateTime.Now.AddYears(1) };

        await Assert.ThrowsAsync<ArgumentException>(() => scenario.Procedures.MarkIssuedAsync(id, local, Scenario.Officer));
    }

    private Task Call(string function, Guid id)
    {
        var p = scenario.Procedures;
        var actor = Scenario.Officer;
        return function switch
        {
            "customised" => p.MarkCustomisedAsync(id, actor),
            "attested" => p.MarkAttestedAsync(id, Scenario.Attestation(), actor),
            "submitted" => p.MarkSubmittedAsync(id, 8001, "AB12CD34", actor),
            "pending" => p.MarkPendingAsync(id, actor),
            "issued" => p.MarkIssuedAsync(id, Scenario.Certificate(), actor),
            "failed" => p.MarkFailedAsync(id, "refused", actor),
            _ => throw new ArgumentOutOfRangeException(nameof(function)),
        };
    }
}

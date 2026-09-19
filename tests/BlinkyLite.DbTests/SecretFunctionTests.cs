using BlinkyLite.Contracts;
using BlinkyLite.Server.Data;

namespace BlinkyLite.DbTests;

[Collection(DatabaseCollection.Name)]
public sealed class SecretFunctionTests(DatabaseFixture db)
{
    private readonly Scenario scenario = new(db);

    [DbFact]
    public async Task Helpdesk_gets_the_PUK_of_one_card_and_the_disclosure_is_audited_with_the_reason()
    {
        var serial = db.NewSerial();
        var reservation = Scenario.Reservation(serial);
        var id = await scenario.Procedures.ReserveIssuanceAsync(reservation, Scenario.Officer);
        await scenario.Procedures.MarkCustomisedAsync(id, Scenario.Officer);

        var secret = await scenario.Procedures.DiscloseSecretAsync(serial, SecretKind.Puk, "  INC-12345 user locked out  ", Scenario.Helpdesk);

        Assert.Equal(id, secret.IssuanceId);
        Assert.Equal(reservation.PukEnvelope, secret.Envelope);
        Assert.Equal(1, await scenario.Scalar("select puk_disclosed_count from blinkylite.card_secrets where issuance_id = $1", id));
        Assert.Equal("INC-12345 user locked out", await scenario.Scalar(
            "select data->>'reason' from blinkylite.audit_events where issuance_id = $1 and action = 'puk.disclosed'", id));
        Assert.Equal("hd@corp.example", await scenario.Scalar(
            "select actor_upn from blinkylite.audit_events where issuance_id = $1 and action = 'puk.disclosed'", id));
    }

    [DbFact]
    public async Task Only_Admin_gets_the_management_key()
    {
        var serial = db.NewSerial();
        var reservation = Scenario.Reservation(serial);
        var id = await scenario.Procedures.ReserveIssuanceAsync(reservation, Scenario.Officer);
        await scenario.Procedures.MarkCustomisedAsync(id, Scenario.Officer);

        foreach (var actor in new[] { Scenario.Officer, Scenario.Helpdesk })
        {
            var error = await Assert.ThrowsAsync<DatabaseRuleException>(
                () => scenario.Procedures.DiscloseSecretAsync(serial, SecretKind.ManagementKey, "INC-1 reason", actor));
            Assert.Equal("BL004", error.SqlState);
        }

        var secret = await scenario.Procedures.DiscloseSecretAsync(serial, SecretKind.ManagementKey, "INC-1 reason", Scenario.Admin);

        Assert.Equal(reservation.MgmtKeyEnvelope, secret.Envelope);
        Assert.Equal(["issuance.reserved", "issuance.customised", "mgmt-key.disclosed"], await scenario.AuditActions(id));
    }

    [DbTheory]
    [InlineData("")]
    [InlineData("    ")]
    [InlineData("abc ")]
    public async Task A_disclosure_without_a_real_reason_is_BL005_and_leaves_no_trace(string reason)
    {
        var (serial, id) = await scenario.IssuanceIn(IssuanceState.Customised);

        var error = await Assert.ThrowsAsync<DatabaseRuleException>(
            () => scenario.Procedures.DiscloseSecretAsync(serial, SecretKind.Puk, reason, Scenario.Helpdesk));

        Assert.Equal("BL005", error.SqlState);
        Assert.Equal("error.reason.required", error.MessageKey);
        Assert.Equal(0, await scenario.Scalar("select puk_disclosed_count from blinkylite.card_secrets where issuance_id = $1", id));
    }

    [DbFact]
    public async Task A_card_without_an_active_envelope_has_nothing_to_disclose()
    {
        // Reserved only: the card may or may not hold this key yet, so it is
        // not the card's PUK until the workstation confirms it.
        var (serial, _) = await scenario.IssuanceIn(IssuanceState.Reserved);

        var error = await Assert.ThrowsAsync<DatabaseRuleException>(
            () => scenario.Procedures.DiscloseSecretAsync(serial, SecretKind.Puk, "INC-2 reason", Scenario.Helpdesk));

        Assert.Equal("BL002", error.SqlState);
    }

    [DbFact]
    public async Task A_Bio_card_has_no_PUK_to_disclose()
    {
        var serial = db.NewSerial();
        var id = await scenario.Procedures.ReserveIssuanceAsync(Scenario.Reservation(serial, hasPuk: false), Scenario.Officer);
        await scenario.Procedures.MarkCustomisedAsync(id, Scenario.Officer);

        var error = await Assert.ThrowsAsync<DatabaseRuleException>(
            () => scenario.Procedures.DiscloseSecretAsync(serial, SecretKind.Puk, "INC-3 reason", Scenario.Admin));

        Assert.Equal("BL002", error.SqlState);
    }

    [DbFact]
    public async Task Management_key_candidates_are_the_active_key_then_newer_reservations_newest_first()
    {
        var serial = db.NewSerial();
        var issued = await scenario.IssuanceIn(IssuanceState.Issued, serial);

        // Two later attempts that died after reservation - either may have
        // reached SET MANAGEMENT KEY before the workstation crashed.
        var crashed1 = await scenario.Procedures.ReserveIssuanceAsync(Scenario.Reservation(serial), Scenario.Officer);
        await scenario.Procedures.MarkFailedAsync(crashed1, "workstation crashed", Scenario.Officer);
        var crashed2 = await scenario.Procedures.ReserveIssuanceAsync(Scenario.Reservation(serial), Scenario.Officer);

        var candidates = await scenario.Procedures.GetManagementKeyCandidatesAsync(serial, Scenario.Officer);

        Assert.Equal([issued, crashed2, crashed1], candidates.Select(c => c.IssuanceId));
        Assert.Equal([CardSecretState.Active, CardSecretState.Reserved, CardSecretState.Reserved], candidates.Select(c => c.State));
        Assert.All(candidates, c => Assert.Equal(0x0A, c.Algorithm));
        Assert.Equal(1L, await scenario.Scalar(
            "select count(*) from blinkylite.audit_events where card_serial = $1 and action = 'mgmt-key.used' " +
            "and jsonb_array_length(data->'secret_ids') = 3", serial));
    }

    [DbFact]
    public async Task Helpdesk_gets_no_management_key_candidates()
    {
        var (serial, _) = await scenario.IssuanceIn(IssuanceState.Issued);

        var error = await Assert.ThrowsAsync<DatabaseRuleException>(
            () => scenario.Procedures.GetManagementKeyCandidatesAsync(serial, Scenario.Helpdesk));

        Assert.Equal("BL004", error.SqlState);
    }
}

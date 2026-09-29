using System.Security.Cryptography;
using BlinkyLite.Contracts;
using BlinkyLite.Server.Data;
using Npgsql;

namespace BlinkyLite.DbTests;

/// <summary>
/// Migration 0008: a key that left service. What matters is not the flag but
/// what the flag switches off - after it, neither of the two roads to a card's
/// secrets may lead anywhere, and they are checked here rather than trusted.
/// </summary>
[Collection(DatabaseCollection.Name)]
public sealed class WithdrawFunctionTests(DatabaseFixture db)
{
    private Procedures Procedures => db.Procedures;

    [DbFact]
    public async Task The_issuance_moves_to_Withdrawn_and_nothing_is_deleted()
    {
        var scenario = new Scenario(db);
        var (serial, issuanceId) = await scenario.IssuanceIn(IssuanceState.Issued);

        await Procedures.WithdrawCardAsync(serial, "klucz skasowany po zwrocie", Scenario.Officer);

        Assert.Equal("Withdrawn", await scenario.StateOf(issuanceId));
        Assert.Equal(1L, await scenario.Scalar("select count(*) from blinkylite.issuances where id = $1", issuanceId));
        Assert.Equal(1L, await scenario.Scalar("select count(*) from blinkylite.card_secrets where card_serial = $1", serial));
        Assert.Equal("Retired", await scenario.Scalar(
            "select state from blinkylite.card_secrets where card_serial = $1", serial));
        Assert.IsType<DBNull>(await scenario.Scalar("select current_issuance_id from blinkylite.cards where serial = $1", serial));

        Assert.Equal("klucz skasowany po zwrocie", await scenario.Scalar(
            "select withdrawn_reason from blinkylite.issuances where id = $1", issuanceId));
        Assert.Equal(Scenario.Officer.Upn, await scenario.Scalar(
            "select withdrawn_by from blinkylite.issuances where id = $1", issuanceId));
        Assert.Contains("card.withdrawn", await scenario.AuditActions(issuanceId));
    }

    /// <summary>The whole point: a record that no longer matches the card stops handing out its secrets.</summary>
    [DbFact]
    public async Task Afterwards_neither_the_PUK_nor_the_management_key_can_be_disclosed()
    {
        var scenario = new Scenario(db);
        var (serial, _) = await scenario.IssuanceIn(IssuanceState.Issued);

        await Procedures.DiscloseSecretAsync(serial, SecretKind.Puk, "zgloszenie INC-1", Scenario.Helpdesk);
        await Procedures.WithdrawCardAsync(serial, "klucz zgubiony, INC-2", Scenario.Officer);

        foreach (var kind in new[] { SecretKind.Puk, SecretKind.ManagementKey })
        {
            var actor = kind == SecretKind.Puk ? Scenario.Helpdesk : Scenario.Admin;
            var error = await Assert.ThrowsAsync<DatabaseRuleException>(
                () => Procedures.DiscloseSecretAsync(serial, kind, "zgloszenie INC-2", actor));

            Assert.Equal("BL002", error.SqlState);
        }
    }

    [DbFact]
    public async Task Afterwards_a_remote_unblock_has_nothing_to_ask_about()
    {
        var scenario = new Scenario(db);
        var (serial, _) = await scenario.IssuanceIn(IssuanceState.Issued);
        await Procedures.WithdrawCardAsync(serial, "klucz skasowany recznie", Scenario.Officer);

        var error = await Assert.ThrowsAsync<DatabaseRuleException>(() => Procedures.RequestUnlockAsync(
            Guid.NewGuid(), serial, "AAA-BBB", RandomNumberGenerator.GetBytes(32), "WS-042", 10, Station));

        Assert.Equal("BL002", error.SqlState);
    }

    [DbFact]
    public async Task A_request_already_waiting_expires_with_the_card()
    {
        var scenario = new Scenario(db);
        var (serial, _) = await scenario.IssuanceIn(IssuanceState.Issued);
        var request = Guid.NewGuid();
        var secret = RandomNumberGenerator.GetBytes(32);
        await Procedures.RequestUnlockAsync(request, serial, "CDE-FGH", secret, "WS-042", 10, Station);
        await Procedures.DecideUnlockAsync(request, approve: true, "rozpoznana przy telefonie", Scenario.Helpdesk);

        await Procedures.WithdrawCardAsync(serial, "klucz skasowany w trakcie rozmowy", Scenario.Officer);

        var delivery = await Procedures.CollectUnlockAsync(request, secret, Station);

        Assert.Equal(UnlockStates.Expired, delivery.State);
        Assert.Null(delivery.Envelope);
    }

    [DbFact]
    public async Task A_reason_is_required_and_the_issuing_roles_alone_may_do_it()
    {
        var scenario = new Scenario(db);
        var (serial, _) = await scenario.IssuanceIn(IssuanceState.Issued);

        var helpdesk = await Assert.ThrowsAsync<DatabaseRuleException>(
            () => Procedures.WithdrawCardAsync(serial, "klucz oddany", Scenario.Helpdesk));
        Assert.Equal("BL004", helpdesk.SqlState);

        var noReason = await Assert.ThrowsAsync<DatabaseRuleException>(
            () => Procedures.WithdrawCardAsync(serial, "  ok", Scenario.Officer));
        Assert.Equal("BL005", noReason.SqlState);
        Assert.Equal(ErrorCodes.ReasonRequired, noReason.MessageKey);

        await Procedures.WithdrawCardAsync(serial, "klucz oddany i skasowany", Scenario.Admin);
    }

    [DbFact]
    public async Task Withdrawing_twice_is_refused_and_an_unknown_card_is_not_found()
    {
        var scenario = new Scenario(db);
        var (serial, _) = await scenario.IssuanceIn(IssuanceState.Issued);
        await Procedures.WithdrawCardAsync(serial, "klucz skasowany po zwrocie", Scenario.Officer);

        var again = await Assert.ThrowsAsync<DatabaseRuleException>(
            () => Procedures.WithdrawCardAsync(serial, "klucz skasowany po zwrocie", Scenario.Officer));
        Assert.Equal("BL001", again.SqlState);
        Assert.Equal(ErrorCodes.CardWithdrawn, again.MessageKey);

        var unknown = await Assert.ThrowsAsync<DatabaseRuleException>(
            () => Procedures.WithdrawCardAsync(db.NewSerial(), "nie ma takiej karty", Scenario.Officer));
        Assert.Equal("BL002", unknown.SqlState);
    }

    /// <summary>
    /// A wiped key is factory again, so it may be issued afresh - and that
    /// issuance has to become the card's current one, not stay behind a
    /// withdrawal that described the key's previous life.
    /// </summary>
    [DbFact]
    public async Task A_withdrawn_card_can_be_issued_again()
    {
        var scenario = new Scenario(db);
        var (serial, first) = await scenario.IssuanceIn(IssuanceState.Issued);
        await Procedures.WithdrawCardAsync(serial, "klucz skasowany przed ponownym wydaniem", Scenario.Officer);

        var second = await scenario.IssuanceIn(IssuanceState.Issued, serial);

        Assert.Equal("Withdrawn", await scenario.StateOf(first));
        Assert.Equal("Issued", await scenario.StateOf(second));
        Assert.Equal(second, await scenario.Scalar(
            "select current_issuance_id from blinkylite.cards where serial = $1", serial));

        // And the new PUK is disclosable again: it belongs to a key that is in
        // service.
        await Procedures.DiscloseSecretAsync(serial, SecretKind.Puk, "wydanie sprawdzone", Scenario.Helpdesk);
    }

    private static readonly Actor Station =
        new("system:unlock", "S-1-0-0", [], System.Net.IPAddress.Loopback);
}

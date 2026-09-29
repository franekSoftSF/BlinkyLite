using System.Net;
using System.Security.Cryptography;
using BlinkyLite.Contracts;
using BlinkyLite.Server.Data;
using Npgsql;

namespace BlinkyLite.DbTests;

/// <summary>
/// Migration 0007: the remote unblock. The state machine is in the functions
/// because the two things that must not happen - two approvals of one request,
/// and one PUK handed out twice - are races, and a race is settled by a row
/// lock, not by the order in which the server happens to call.
/// </summary>
[Collection(DatabaseCollection.Name)]
public sealed class UnlockFunctionTests(DatabaseFixture db)
{
    /// <summary>
    /// The workstation, with nobody signed in: no roles at all, which is what
    /// the three anonymous endpoints pass.
    /// </summary>
    private static readonly Actor Station = new("system:unlock", "S-1-0-0", [], IPAddress.Parse("10.4.0.9"));

    private Procedures Procedures => db.Procedures;

    [DbFact]
    public async Task A_card_without_an_active_PUK_has_nothing_to_unblock_with()
    {
        var unknown = db.NewSerial();

        var error = await Assert.ThrowsAsync<DatabaseRuleException>(
            () => Procedures.RequestUnlockAsync(Guid.NewGuid(), unknown, "AAA-BBB", Hash(), "WS-042", 10, Station));

        Assert.Equal("BL002", error.SqlState);
        Assert.Equal(ErrorCodes.NotFound, error.MessageKey);
    }

    [DbFact]
    public async Task While_it_waits_the_workstation_learns_the_state_and_nothing_else()
    {
        var (_, request, secret) = await Asked();

        var delivery = await Procedures.CollectUnlockAsync(request, secret, Station);

        Assert.Equal(UnlockStates.Pending, delivery.State);
        Assert.Null(delivery.Envelope);
        Assert.Null(delivery.IssuanceId);
    }

    [DbFact]
    public async Task An_approval_hands_the_envelope_over_exactly_once()
    {
        var (serial, request, secret) = await Asked();
        await Procedures.DecideUnlockAsync(request, approve: true, "Anna z numeru 412", Scenario.Helpdesk);

        var first = await Procedures.CollectUnlockAsync(request, secret, Station);
        var second = await Procedures.CollectUnlockAsync(request, secret, Station);

        Assert.Equal(UnlockStates.Delivered, first.State);
        Assert.NotNull(first.Envelope);
        Assert.Equal((short)1, first.KekVersion);

        Assert.Equal(UnlockStates.Delivered, second.State);
        Assert.Null(second.Envelope);

        // The same event an ordinary reveal writes, and the same counter: a PUK
        // that left the server looks the same however it was asked for.
        Assert.Equal(1L, await Count(serial, "puk.disclosed"));
        Assert.Equal(1, (int)(await Scalar(
            "select puk_disclosed_count from blinkylite.card_secrets where card_serial = $1 and state = 'Active'", serial))!);
        Assert.Equal("Anna z numeru 412", await Scalar(
            "select data->>'reason' from blinkylite.audit_events where action = 'puk.disclosed' and card_serial = $1", serial));
        Assert.Equal(true, await Scalar(
            "select (data->>'remote')::boolean from blinkylite.audit_events where action = 'puk.disclosed' and card_serial = $1", serial));
    }

    [DbFact]
    public async Task Eight_collections_at_once_hand_the_envelope_to_one_of_them()
    {
        var (serial, request, secret) = await Asked();
        await Procedures.DecideUnlockAsync(request, approve: true, "rozpoznana przy telefonie", Scenario.Helpdesk);

        var attempts = Enumerable.Range(0, 8).Select(_ => Task.Run(async () =>
            (await Procedures.CollectUnlockAsync(request, secret, Station)).Envelope is not null));

        var won = await Task.WhenAll(attempts);

        Assert.Single(won, delivered => delivered);
        Assert.Equal(1L, await Count(serial, "puk.disclosed"));
    }

    [DbFact]
    public async Task The_code_alone_is_not_enough_a_wrong_secret_is_answered_like_an_unknown_request()
    {
        var (_, request, _) = await Asked();
        await Procedures.DecideUnlockAsync(request, approve: true, "sprawdzone przy telefonie", Scenario.Helpdesk);

        var collect = await Assert.ThrowsAsync<DatabaseRuleException>(
            () => Procedures.CollectUnlockAsync(request, Hash(), Station));
        var finish = await Assert.ThrowsAsync<DatabaseRuleException>(
            () => Procedures.FinishUnlockAsync(request, Hash(), true, null, Station));

        Assert.Equal("BL002", collect.SqlState);
        Assert.Equal("BL002", finish.SqlState);
    }

    [DbFact]
    public async Task Deciding_needs_a_role_a_reason_and_a_request_nobody_has_decided_yet()
    {
        var (_, request, _) = await Asked();

        var noRole = await Assert.ThrowsAsync<DatabaseRuleException>(
            () => Procedures.DecideUnlockAsync(request, approve: true, "z telefonu", Station));
        Assert.Equal("BL004", noRole.SqlState);

        var noReason = await Assert.ThrowsAsync<DatabaseRuleException>(
            () => Procedures.DecideUnlockAsync(request, approve: true, "  ok", Scenario.Helpdesk));
        Assert.Equal("BL005", noReason.SqlState);
        Assert.Equal(ErrorCodes.ReasonRequired, noReason.MessageKey);

        await Procedures.DecideUnlockAsync(request, approve: true, "rozpoznana przy telefonie", Scenario.Helpdesk);

        var twice = await Assert.ThrowsAsync<DatabaseRuleException>(
            () => Procedures.DecideUnlockAsync(request, approve: false, "jednak nie", Scenario.Admin));
        Assert.Equal("BL001", twice.SqlState);
        Assert.Equal(ErrorCodes.UnlockInvalidState, twice.MessageKey);
    }

    [DbFact]
    public async Task A_refusal_is_final_and_gives_out_nothing()
    {
        var (serial, request, secret) = await Asked();

        await Procedures.DecideUnlockAsync(request, approve: false, "nie rozpoznaje osoby dzwoniacej", Scenario.Officer);
        var delivery = await Procedures.CollectUnlockAsync(request, secret, Station);

        Assert.Equal(UnlockStates.Refused, delivery.State);
        Assert.Null(delivery.Envelope);
        Assert.Equal(0L, await Count(serial, "puk.disclosed"));
        Assert.Equal(1L, await Count(serial, "unlock.refused"));
    }

    [DbFact]
    public async Task The_waiting_list_is_for_the_roles_that_may_reveal_a_PUK_and_names_the_person()
    {
        var (serial, request, _) = await Asked();

        var mine = await Procedures.UnlockWaitingAsync(Scenario.Helpdesk);
        var row = mine.Single(r => r.Id == request);

        Assert.Equal(serial, row.CardSerial);
        Assert.Equal("Jan Kowalski", row.TargetDisplayName);
        Assert.Equal("WS-042", row.Workstation);
        Assert.Equal("10.4.0.9", row.SourceIp);

        var refused = await Assert.ThrowsAsync<DatabaseRuleException>(() => Procedures.UnlockWaitingAsync(Station));
        Assert.Equal("BL004", refused.SqlState);
    }

    [DbFact]
    public async Task A_second_call_about_the_same_key_leaves_one_live_code()
    {
        var serial = await Issued();
        var (first, firstSecret) = (Guid.NewGuid(), Hash());
        await Procedures.RequestUnlockAsync(first, serial, Code(), firstSecret, "WS-042", 10, Station);
        var second = Guid.NewGuid();
        await Procedures.RequestUnlockAsync(second, serial, Code(), Hash(), "WS-042", 10, Station);

        var waiting = await Procedures.UnlockWaitingAsync(Scenario.Helpdesk);

        Assert.DoesNotContain(waiting, r => r.Id == first);
        Assert.Contains(waiting, r => r.Id == second);
        Assert.Equal(UnlockStates.Expired, (await Procedures.CollectUnlockAsync(first, firstSecret, Station)).State);
    }

    [DbFact]
    public async Task A_code_nobody_approved_in_time_expires_and_cannot_be_approved_afterwards()
    {
        var serial = await Issued();
        var request = Guid.NewGuid();
        var secret = Hash();

        // Zero minutes: the row is already past its time when the next call
        // looks at it, which is what a code left on a desk overnight looks like.
        await Procedures.RequestUnlockAsync(request, serial, Code(), secret, "WS-042", 0, Station);

        var late = await Assert.ThrowsAsync<DatabaseRuleException>(
            () => Procedures.DecideUnlockAsync(request, approve: true, "oddzwonilem po godzinie", Scenario.Helpdesk));

        Assert.Equal("BL001", late.SqlState);
        Assert.Equal(UnlockStates.Expired, (await Procedures.CollectUnlockAsync(request, secret, Station)).State);
    }

    [DbFact]
    public async Task What_the_key_said_is_written_down_and_only_once()
    {
        var (serial, request, secret) = await Asked();
        await Procedures.DecideUnlockAsync(request, approve: true, "rozpoznana przy telefonie", Scenario.Helpdesk);

        var early = await Assert.ThrowsAsync<DatabaseRuleException>(
            () => Procedures.FinishUnlockAsync(request, secret, true, null, Station));
        Assert.Equal("BL001", early.SqlState);

        await Procedures.CollectUnlockAsync(request, secret, Station);
        await Procedures.FinishUnlockAsync(request, secret, false, "PUK odrzucony przez klucz", Station);

        Assert.Equal(1L, await Count(serial, "unlock.failed"));
        Assert.Equal("PUK odrzucony przez klucz", await Scalar(
            "select data->>'error' from blinkylite.audit_events where action = 'unlock.failed' and card_serial = $1", serial));

        var twice = await Assert.ThrowsAsync<DatabaseRuleException>(
            () => Procedures.FinishUnlockAsync(request, secret, true, null, Station));
        Assert.Equal("BL001", twice.SqlState);
    }

    [DbFact]
    public async Task The_whole_way_through_leaves_four_events_in_order()
    {
        var (serial, request, secret) = await Asked();

        await Procedures.DecideUnlockAsync(request, approve: true, "Anna z numeru 412", Scenario.Helpdesk);
        await Procedures.CollectUnlockAsync(request, secret, Station);
        await Procedures.FinishUnlockAsync(request, secret, true, null, Station);

        Assert.Equal(
            ["unlock.requested", "unlock.approved", "puk.disclosed", "unlock.completed"],
            await Actions(serial));

        // Who approved it is on the events the workstation caused as well; the
        // workstation itself is not a person and must not look like one.
        Assert.Equal(Scenario.Helpdesk.Upn, await Scalar(
            "select data->>'approved_by' from blinkylite.audit_events where action = 'unlock.completed' and card_serial = $1", serial));
        Assert.Equal("system:unlock", await Scalar(
            "select actor_upn from blinkylite.audit_events where action = 'unlock.requested' and card_serial = $1", serial));
    }

    private async Task<(long Serial, Guid Request, byte[] Secret)> Asked()
    {
        var serial = await Issued();
        var request = Guid.NewGuid();
        var secret = Hash();
        await Procedures.RequestUnlockAsync(request, serial, Code(), secret, "WS-042", 10, Station);
        return (serial, request, secret);
    }

    private async Task<long> Issued()
    {
        var (serial, _) = await new Scenario(db).IssuanceIn(IssuanceState.Issued);
        return serial;
    }

    private static byte[] Hash() => RandomNumberGenerator.GetBytes(32);

    private static string Code() => RandomNumberGenerator.GetString("ABCDEFGHJKMNPQRSTUVWXYZ23456789", 7);

    private async Task<long> Count(long serial, string action) =>
        (long)(await Scalar("select count(*) from blinkylite.audit_events where card_serial = $1 and action = $2", serial, action))!;

    private async Task<List<string>> Actions(long serial)
    {
        var actions = new List<string>();
        await using var command = db.AppDataSource.CreateCommand(
            "select action from blinkylite.audit_events where card_serial = $1 "
            + "and (action like 'unlock.%' or action = 'puk.disclosed') order by id");
        command.Parameters.Add(new NpgsqlParameter { Value = serial });
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            actions.Add(reader.GetString(0));
        }

        return actions;
    }

    private async Task<object?> Scalar(string sql, params object[] args)
    {
        await using var command = db.AppDataSource.CreateCommand(sql);
        foreach (var arg in args)
        {
            command.Parameters.Add(new NpgsqlParameter { Value = arg });
        }

        return await command.ExecuteScalarAsync();
    }
}

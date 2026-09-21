using System.Net;
using System.Security.Cryptography;
using BlinkyLite.Contracts;
using BlinkyLite.Server.Data;
using Npgsql;

namespace BlinkyLite.DbTests;

/// <summary>
/// Migration 0006: the second factor's rules are in the functions, so that a
/// bug in the server is not enough to sign in with a spent code.
/// </summary>
[Collection(DatabaseCollection.Name)]
public sealed class TotpFunctionTests(DatabaseFixture db)
{
    private static int next = 7000;

    private Procedures Procedures => db.Procedures;

    [DbFact]
    public async Task An_unconfirmed_secret_is_state_only_and_signs_nobody_in()
    {
        var op = Operator();
        var envelope = RandomNumberGenerator.GetBytes(48);

        await Procedures.BeginTotpAsync(envelope, 1, op);
        var state = await Procedures.GetTotpAsync(op);

        Assert.Equal(envelope, state!.SecretEnvelope);
        Assert.False(state.Confirmed);

        var error = await Assert.ThrowsAsync<DatabaseRuleException>(() => Procedures.AcceptTotpAsync(100, op));
        Assert.Equal("BL008", error.SqlState);
        Assert.Equal(ErrorCodes.TotpSetupRequired, error.MessageKey);
    }

    [DbFact]
    public async Task Confirming_stores_the_backup_codes_and_audits_the_setup_and_the_sign_in()
    {
        var op = Operator();
        await Procedures.BeginTotpAsync(RandomNumberGenerator.GetBytes(48), 1, op);

        await Procedures.ConfirmTotpAsync(100, Hashes(10), op);

        Assert.True((await Procedures.GetTotpAsync(op))!.Confirmed);
        Assert.Equal(10L, await Superuser("select count(*) from blinkylite.operator_backup_codes where operator_sid = $1", op.Sid!));
        Assert.Equal(["totp.setup-started", "totp.enrolled", "auth.login"], await Actions(op.Sid!));
    }

    [DbFact]
    public async Task A_step_is_accepted_once_and_never_an_older_one()
    {
        var op = await Enrolled(step: 100);

        await Procedures.AcceptTotpAsync(101, op);

        foreach (var spent in new long[] { 101, 100, 99 })
        {
            var error = await Assert.ThrowsAsync<DatabaseRuleException>(() => Procedures.AcceptTotpAsync(spent, op));
            Assert.Equal("BL006", error.SqlState);
            Assert.Equal(ErrorCodes.TotpInvalid, error.MessageKey);
        }

        await Procedures.AcceptTotpAsync(102, op);
    }

    [DbFact]
    public async Task The_same_step_sent_twice_at_once_signs_in_once()
    {
        var op = await Enrolled(step: 100);

        var attempts = Enumerable.Range(0, 8).Select(_ => Task.Run(async () =>
        {
            try
            {
                await Procedures.AcceptTotpAsync(150, op);
                return true;
            }
            catch (DatabaseRuleException e) when (e.SqlState == "BL006")
            {
                return false;
            }
        }));

        var results = await Task.WhenAll(attempts);

        Assert.Single(results, won => won);
    }

    [DbFact]
    public async Task A_backup_code_is_spent_by_its_first_use()
    {
        var hashes = Hashes(10);
        var op = await Enrolled(step: 100, hashes);

        Assert.Equal(9, await Procedures.UseBackupCodeAsync(hashes[4], op));

        var error = await Assert.ThrowsAsync<DatabaseRuleException>(() => Procedures.UseBackupCodeAsync(hashes[4], op));
        Assert.Equal("BL006", error.SqlState);
        Assert.Equal(8, await Procedures.UseBackupCodeAsync(hashes[5], op));
    }

    [DbFact]
    public async Task A_backup_code_of_another_operator_does_not_open_this_one()
    {
        var hashes = Hashes(10);
        await Enrolled(step: 100, hashes);
        var other = await Enrolled(step: 100);

        var error = await Assert.ThrowsAsync<DatabaseRuleException>(() => Procedures.UseBackupCodeAsync(hashes[0], other));

        Assert.Equal("BL006", error.SqlState);
    }

    [DbFact]
    public async Task A_confirmed_second_factor_is_not_replaced_by_starting_again()
    {
        var op = await Enrolled(step: 100);

        var error = await Assert.ThrowsAsync<DatabaseRuleException>(
            () => Procedures.BeginTotpAsync(RandomNumberGenerator.GetBytes(48), 1, op));

        Assert.Equal("BL007", error.SqlState);
        Assert.Equal(ErrorCodes.TotpAlreadyConfigured, error.MessageKey);
    }

    [DbFact]
    public async Task Nobody_reads_another_operators_state()
    {
        var op = await Enrolled(step: 100);
        var nosy = op with { Sid = "S-1-5-21-100-200-300-9" };

        await using var command = db.AppDataSource.CreateCommand(
            "select * from blinkylite.bl_totp_state($1, $2, $3, $4, $5)");
        command.Parameters.Add(new NpgsqlParameter { Value = op.Sid });
        command.Parameters.Add(new NpgsqlParameter { Value = nosy.Upn });
        command.Parameters.Add(new NpgsqlParameter { Value = nosy.Sid });
        command.Parameters.Add(new NpgsqlParameter { Value = new[] { "Admin" } });
        command.Parameters.Add(new NpgsqlParameter { Value = IPAddress.Loopback });

        var error = await Assert.ThrowsAsync<PostgresException>(() => command.ExecuteReaderAsync());

        Assert.Equal("BL004", error.SqlState);
    }

    [DbFact]
    public async Task Only_an_Admin_resets_and_never_their_own()
    {
        var op = await Enrolled(step: 100);
        var admin = Operator([Role.Admin]);

        var officer = await Assert.ThrowsAsync<DatabaseRuleException>(
            () => Procedures.ResetTotpAsync(op.Sid!, "lost phone", Scenario.Officer));
        Assert.Equal("BL004", officer.SqlState);

        var own = await Assert.ThrowsAsync<DatabaseRuleException>(
            () => Procedures.ResetTotpAsync(admin.Sid!, "lost phone", admin));
        Assert.Equal("BL004", own.SqlState);

        var noReason = await Assert.ThrowsAsync<DatabaseRuleException>(
            () => Procedures.ResetTotpAsync(op.Sid!, "  ", admin));
        Assert.Equal("BL005", noReason.SqlState);

        await Procedures.ResetTotpAsync(op.Sid!, "INC-7 lost phone", admin);

        Assert.Null(await Procedures.GetTotpAsync(op));
        Assert.Equal(0L, await Superuser("select count(*) from blinkylite.operator_backup_codes where operator_sid = $1", op.Sid!));
        Assert.Equal("INC-7 lost phone", await Superuser(
            "select data->>'reason' from blinkylite.audit_events where action = 'totp.reset' and data->>'operator_sid' = $1", op.Sid!));

        // And the operator can set up a new one.
        await Procedures.BeginTotpAsync(RandomNumberGenerator.GetBytes(48), 1, op);
    }

    private static Actor Operator(Role[]? roles = null)
    {
        var id = Interlocked.Increment(ref next);
        return new Actor($"op{id}@corp.example", $"S-1-5-21-100-200-300-{id}", roles ?? [Role.Helpdesk], IPAddress.Loopback);
    }

    private async Task<Actor> Enrolled(long step, List<byte[]>? hashes = null)
    {
        var op = Operator();
        await Procedures.BeginTotpAsync(RandomNumberGenerator.GetBytes(48), 1, op);
        await Procedures.ConfirmTotpAsync(step, hashes ?? Hashes(10), op);
        return op;
    }

    private static List<byte[]> Hashes(int count) =>
        Enumerable.Range(0, count).Select(_ => RandomNumberGenerator.GetBytes(32)).ToList();

    private async Task<List<string>> Actions(string sid)
    {
        var actions = new List<string>();
        await using var source = NpgsqlDataSource.Create(db.Superuser);
        await using var command = source.CreateCommand(
            "select action from blinkylite.audit_events where actor_sid = $1 order by id");
        command.Parameters.Add(new NpgsqlParameter { Value = sid });
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            actions.Add(reader.GetString(0));
        }

        return actions;
    }

    private async Task<object?> Superuser(string sql, params object[] args)
    {
        // The app role cannot read these tables at all - which is the point -
        // so the checks look from outside.
        await using var source = NpgsqlDataSource.Create(db.Superuser);
        await using var command = source.CreateCommand(sql);
        foreach (var arg in args)
        {
            command.Parameters.Add(new NpgsqlParameter { Value = arg });
        }

        return await command.ExecuteScalarAsync();
    }
}

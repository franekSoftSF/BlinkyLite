using System.Net;
using BlinkyLite.Contracts;
using BlinkyLite.Server.Data;
using Npgsql;

namespace BlinkyLite.DbTests;

[Collection(DatabaseCollection.Name)]
public sealed class ConcurrencyAndAuditTests(DatabaseFixture db)
{
    private readonly Scenario scenario = new(db);

    [DbFact]
    public async Task Two_simultaneous_reservations_of_one_card_yield_exactly_one_issuance()
    {
        // Repeated, because one lucky interleaving proves nothing about a race.
        for (var round = 0; round < 20; round++)
        {
            var serial = db.NewSerial();
            using var start = new ManualResetEventSlim();

            var attempts = Enumerable.Range(0, 2).Select(_ => Task.Run(async () =>
            {
                start.Wait();
                try
                {
                    await scenario.Procedures.ReserveIssuanceAsync(Scenario.Reservation(serial), Scenario.Officer);
                    return "won";
                }
                catch (DatabaseRuleException e) when (e.SqlState == "BL003")
                {
                    return "BL003";
                }
            })).ToArray();

            start.Set();
            var outcomes = await Task.WhenAll(attempts);

            Assert.Equal(["BL003", "won"], outcomes.Order());
            Assert.Equal(1L, await scenario.Scalar("select count(*) from blinkylite.issuances where card_serial = $1", serial));
            Assert.Equal(1L, await scenario.Scalar("select count(*) from blinkylite.card_secrets where card_serial = $1", serial));
        }
    }

    [DbFact]
    public async Task bl_audit_records_sign_in_and_a_refused_sign_in_without_a_SID()
    {
        var marker = Guid.NewGuid().ToString("N");
        var stranger = new Actor($"{marker}@corp.example", Sid: null, Roles: [], SourceIp: IPAddress.Parse("10.1.2.3"));

        await scenario.Procedures.AuditAsync(AuditAction.AuthLogin, new { marker }, Scenario.Officer);
        await scenario.Procedures.AuditAsync(AuditAction.AuthDenied, new { reason = "bad-password" }, stranger);

        Assert.Equal(1L, await scenario.Scalar(
            "select count(*) from blinkylite.audit_events where action = 'auth.login' and data->>'marker' = $1", marker));
        Assert.Equal("10.1.2.3", await scenario.Scalar(
            "select host(source_ip) from blinkylite.audit_events where actor_upn = $1", $"{marker}@corp.example"));
    }

    [DbFact]
    public async Task bl_audit_cannot_be_used_to_forge_a_disclosure_or_an_issuance()
    {
        await using var connection = new NpgsqlConnection(db.App);
        await connection.OpenAsync();

        foreach (var action in new[] { "puk.disclosed", "issuance.issued", "mgmt-key.used" })
        {
            await using var command = new NpgsqlCommand(
                "select blinkylite.bl_audit($1, '{}'::jsonb, 'x@corp.example', 'S-1-5-21-1-2-3-4', '{Admin}', null)", connection);
            command.Parameters.Add(new NpgsqlParameter { Value = action });

            var error = await Assert.ThrowsAsync<PostgresException>(() => command.ExecuteScalarAsync());
            Assert.Equal(PostgresErrorCodes.InvalidParameterValue, error.SqlState);
        }
    }

    [DbFact]
    public async Task A_known_actor_must_have_a_SID()
    {
        var noSid = Scenario.Officer with { Sid = null };

        var error = await Assert.ThrowsAsync<PostgresException>(
            () => scenario.Procedures.AuditAsync(AuditAction.AuthLogin, null, noSid));

        Assert.Equal(PostgresErrorCodes.CheckViolation, error.SqlState);
    }
}

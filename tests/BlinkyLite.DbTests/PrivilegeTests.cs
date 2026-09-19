using BlinkyLite.Contracts;
using Npgsql;

namespace BlinkyLite.DbTests;

/// <summary>
/// The rules in docs/07 are enforced by PostgreSQL, not by the server's good
/// manners. These tests connect as the roles the server and a report would use
/// and try to go around the functions.
/// </summary>
[Collection(DatabaseCollection.Name)]
public sealed class PrivilegeTests(DatabaseFixture db)
{
    private static readonly string[] Tables = ["cards", "issuances", "card_secrets", "audit_events", "schema_migrations"];

    public static TheoryData<string, string> WritesOnEveryTable()
    {
        var data = new TheoryData<string, string>();
        foreach (var role in new[] { "app", "readonly" })
        {
            foreach (var table in Tables)
            {
                data.Add(role, table);
            }
        }

        return data;
    }

    [DbTheory]
    [MemberData(nameof(WritesOnEveryTable))]
    public async Task Neither_the_application_nor_the_readonly_role_can_write_any_table(string role, string table)
    {
        var (serial, _) = await new Scenario(db).IssuanceIn(IssuanceState.Issued);
        await using var connection = await Open(role);

        string[] statements =
        [
            $"insert into blinkylite.{table} default values",
            $"update blinkylite.{table} set {FirstColumn(table)} = {FirstColumn(table)}",
            $"delete from blinkylite.{table}",
            $"truncate blinkylite.{table}",
        ];

        foreach (var sql in statements)
        {
            var error = await Assert.ThrowsAsync<PostgresException>(() => Execute(connection, sql));
            Assert.True(error.SqlState == PostgresErrorCodes.InsufficientPrivilege, $"{role}: '{sql}' failed with {error.SqlState}, not 42501");
        }

        Assert.Equal(1L, await new Scenario(db).Scalar("select count(*) from blinkylite.cards where serial = $1", serial));
    }

    [DbTheory]
    [InlineData("app", "puk_envelope")]
    [InlineData("app", "mgmt_key_envelope")]
    [InlineData("readonly", "puk_envelope")]
    [InlineData("readonly", "mgmt_key_envelope")]
    public async Task Envelopes_cannot_be_selected_around_bl_secret_disclose(string role, string column)
    {
        await using var connection = await Open(role);

        var error = await Assert.ThrowsAsync<PostgresException>(
            () => Execute(connection, $"select {column} from blinkylite.card_secrets"));
        Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, error.SqlState);

        var starError = await Assert.ThrowsAsync<PostgresException>(
            () => Execute(connection, "select * from blinkylite.card_secrets"));
        Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, starError.SqlState);
    }

    [DbFact]
    public async Task The_application_can_execute_exactly_the_bl_API_and_PUBLIC_nothing()
    {
        await using var connection = await Open("superuser");

        var executable = await Strings(connection,
            """
            select p.proname from pg_proc p join pg_namespace n on n.oid = p.pronamespace
            where n.nspname = 'blinkylite' and has_function_privilege('blinkylite_app', p.oid, 'EXECUTE')
            order by 1
            """);
        var publicExecutable = await Strings(connection,
            """
            select p.proname from pg_proc p join pg_namespace n on n.oid = p.pronamespace
            where n.nspname = 'blinkylite'
              and exists (select 1 from aclexplode(coalesce(p.proacl, acldefault('f', p.proowner))) a
                          where a.grantee = 0 and a.privilege_type = 'EXECUTE')
            """);

        Assert.Equal(
            ["bl_audit", "bl_issuance_attested", "bl_issuance_customised", "bl_issuance_failed", "bl_issuance_issued",
             "bl_issuance_pending", "bl_issuance_reserve", "bl_issuance_submitted", "bl_mgmt_key_candidates",
             "bl_secret_disclose"],
            executable);
        Assert.Empty(publicExecutable);
    }

    [DbFact]
    public async Task Every_bl_function_is_security_definer_with_a_fixed_search_path_and_owned_by_the_owner()
    {
        await using var connection = await Open("superuser");

        var wrong = await Strings(connection,
            """
            select p.proname from pg_proc p join pg_namespace n on n.oid = p.pronamespace
            where n.nspname = 'blinkylite' and p.proname like 'bl\_%'
              and (not p.prosecdef
                   or not coalesce('search_path=blinkylite, pg_temp' = any (p.proconfig), false)
                   or pg_get_userbyid(p.proowner) <> 'blinkylite_owner')
            """);

        Assert.Empty(wrong);
    }

    [DbFact]
    public async Task The_application_role_is_not_a_member_of_the_owner()
    {
        await using var connection = await Open("superuser");

        var member = await Scalar(connection, "select pg_has_role('blinkylite_app', 'blinkylite_owner', 'member')");

        Assert.Equal(false, member);
    }

    [DbFact]
    public async Task The_application_cannot_call_the_internal_helpers()
    {
        await using var connection = await Open("app");

        var error = await Assert.ThrowsAsync<PostgresException>(() => Execute(connection,
            "select blinkylite._bl_write_audit('puk.disclosed', null, null, '{}', 'x', 'S-1-1', '{}', null)"));

        Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, error.SqlState);
    }

    private async Task<NpgsqlConnection> Open(string role)
    {
        var connection = new NpgsqlConnection(role switch
        {
            "app" => db.App,
            "readonly" => db.Readonly,
            "superuser" => db.Superuser,
            _ => throw new ArgumentOutOfRangeException(nameof(role)),
        });
        await connection.OpenAsync();
        return connection;
    }

    // Not audit_events.id: an UPDATE of a GENERATED ALWAYS column is refused
    // before privileges are even checked, which would prove nothing here.
    private static string FirstColumn(string table) => table switch
    {
        "cards" => "serial",
        "schema_migrations" => "version",
        "audit_events" => "action",
        _ => "id",
    };

    private static async Task Execute(NpgsqlConnection connection, string sql)
    {
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<object?> Scalar(NpgsqlConnection connection, string sql)
    {
        await using var command = new NpgsqlCommand(sql, connection);
        return await command.ExecuteScalarAsync();
    }

    private static async Task<List<string>> Strings(NpgsqlConnection connection, string sql)
    {
        var values = new List<string>();
        await using var command = new NpgsqlCommand(sql, connection);
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            values.Add(reader.GetString(0));
        }

        return values;
    }
}

using Npgsql;

namespace BlinkyLite.DbTests;

[Collection(DatabaseCollection.Name)]
public sealed class PostgresTests
{
    [DbFact]
    public async Task Database_is_PostgreSQL_16_or_newer()
    {
        await using var connection = new NpgsqlConnection(DbFactAttribute.ConnectionString);
        await connection.OpenAsync();

        await using var command = new NpgsqlCommand("show server_version_num", connection);
        var version = int.Parse((string)(await command.ExecuteScalarAsync())!);

        // 16 is the minimum the README and docs/07 promise; the migrations are
        // written and tested against nothing older.
        Assert.True(version >= 160000, $"PostgreSQL {version} is older than 16.");
    }
}

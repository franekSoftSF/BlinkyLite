using System.Security.Cryptography;
using System.Text;
using Npgsql;

namespace BlinkyLite.Server.Data;

/// <summary>One numbered script from <c>db/migrations</c>, embedded in the server.</summary>
public sealed record Migration(string Version, string Sql, string Sha256);

/// <summary>The database and this build disagree about the schema; the server must not start.</summary>
public sealed class MigrationException(string message) : Exception(message);

/// <summary>
/// Applies and verifies the SQL migrations. SQL is the source of truth for the
/// schema; the NHibernate mappings are checked against it, never the reverse.
/// </summary>
public static class Migrations
{
    public const string OwnerRole = "blinkylite_owner";
    public const string Schema = "blinkylite";

    // Any fixed number: two servers started with --migrate at once must take
    // turns, or both would apply 0001 and one would fail half-way.
    private const long AdvisoryLockKey = 0x426C_696E_6B79_4C69;

    private const string ResourcePrefix = "migrations/";

    public static IReadOnlyList<Migration> Embedded { get; } = LoadEmbedded();

    /// <summary>
    /// Applies every missing migration, each in its own transaction, as
    /// <see cref="OwnerRole"/>. Returns how many were applied.
    /// </summary>
    public static async Task<int> ApplyAsync(NpgsqlDataSource owner, Action<string> log, CancellationToken ct = default)
    {
        await using var connection = await owner.OpenConnectionAsync(ct);

        // Run as anyone else, the tables would belong to that role and every
        // SECURITY DEFINER function would run with its rights - as a superuser,
        // if that is who happened to run --migrate.
        var user = (string)(await ScalarAsync(connection, "select current_user", ct))!;
        if (user != OwnerRole)
        {
            throw new MigrationException(
                $"Migrations must run as {OwnerRole}, not {user}: tables and SECURITY DEFINER functions would belong to the wrong role.");
        }

        await ExecuteAsync(connection, $"select pg_advisory_lock({AdvisoryLockKey})", ct);
        try
        {
            await ExecuteAsync(connection,
                $"""
                create schema if not exists {Schema};
                create table if not exists {Schema}.schema_migrations (
                    version    text        primary key,
                    sha256     text        not null,
                    applied_at timestamptz not null default now());
                """, ct);

            var applied = await ReadAppliedAsync(connection, ct);
            var pending = Compare(applied);

            foreach (var migration in pending)
            {
                await using var transaction = await connection.BeginTransactionAsync(ct);

                await ExecuteAsync(connection, $"set local search_path = {Schema}, pg_temp", ct, transaction);
                await ExecuteAsync(connection, migration.Sql, ct, transaction);

                await using (var record = new NpgsqlCommand(
                    $"insert into {Schema}.schema_migrations (version, sha256) values ($1, $2)", connection, transaction))
                {
                    record.Parameters.Add(new NpgsqlParameter { Value = migration.Version });
                    record.Parameters.Add(new NpgsqlParameter { Value = migration.Sha256 });
                    await record.ExecuteNonQueryAsync(ct);
                }

                await transaction.CommitAsync(ct);
                log($"applied migration {migration.Version}");
            }

            return pending.Count;
        }
        finally
        {
            await ExecuteAsync(connection, $"select pg_advisory_unlock({AdvisoryLockKey})", CancellationToken.None);
        }
    }

    /// <summary>
    /// Refuses to let the server run against a database whose migrations are
    /// missing, changed after being applied, or newer than this build. Needs
    /// only SELECT on schema_migrations, so it runs as the application role.
    /// </summary>
    public static async Task VerifyAsync(NpgsqlDataSource app, CancellationToken ct = default)
    {
        await using var connection = await app.OpenConnectionAsync(ct);

        var exists = (bool)(await ScalarAsync(connection,
            $"select to_regclass('{Schema}.schema_migrations') is not null", ct))!;
        var applied = exists ? await ReadAppliedAsync(connection, ct) : new Dictionary<string, string>();

        var pending = Compare(applied);
        if (pending.Count > 0)
        {
            throw new MigrationException(
                $"The database is missing migrations {string.Join(", ", pending.Select(m => m.Version))}. " +
                $"Run the server with --migrate (connection string Owner, role {OwnerRole}).");
        }
    }

    /// <summary>Returns the migrations still to apply; throws if history disagrees with this build.</summary>
    private static List<Migration> Compare(IReadOnlyDictionary<string, string> applied)
    {
        var known = Embedded.ToDictionary(m => m.Version, StringComparer.Ordinal);

        var unknown = applied.Keys.Where(v => !known.ContainsKey(v)).Order(StringComparer.Ordinal).ToList();
        if (unknown.Count > 0)
        {
            throw new MigrationException(
                $"The database has migrations this build does not know: {string.Join(", ", unknown)}. " +
                "It was migrated by a newer server; running an older one against it is not supported.");
        }

        foreach (var (version, sha256) in applied)
        {
            if (!string.Equals(known[version].Sha256, sha256, StringComparison.Ordinal))
            {
                throw new MigrationException(
                    $"Migration {version} was changed after it was applied (sha256 {sha256} in the database, " +
                    $"{known[version].Sha256} in this build). Applied migrations are immutable: restore the file " +
                    "and put the change in a new migration.");
            }
        }

        return Embedded.Where(m => !applied.ContainsKey(m.Version)).ToList();
    }

    private static async Task<Dictionary<string, string>> ReadAppliedAsync(NpgsqlConnection connection, CancellationToken ct)
    {
        var applied = new Dictionary<string, string>(StringComparer.Ordinal);
        await using var command = new NpgsqlCommand($"select version, sha256 from {Schema}.schema_migrations", connection);
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            applied[reader.GetString(0)] = reader.GetString(1);
        }

        return applied;
    }

    private static IReadOnlyList<Migration> LoadEmbedded()
    {
        var assembly = typeof(Migrations).Assembly;
        var migrations = new List<Migration>();

        foreach (var name in assembly.GetManifestResourceNames()
                     .Where(n => n.StartsWith(ResourcePrefix, StringComparison.Ordinal) && n.EndsWith(".sql", StringComparison.Ordinal))
                     .Order(StringComparer.Ordinal))
        {
            using var stream = assembly.GetManifestResourceStream(name)!;
            using var reader = new StreamReader(stream, Encoding.UTF8);

            // Hashed with LF endings: a checkout that turned the file into CRLF
            // has not changed the migration, and must not stop the server.
            var sql = reader.ReadToEnd().Replace("\r\n", "\n", StringComparison.Ordinal);
            var sha256 = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(sql)));

            migrations.Add(new Migration(name[ResourcePrefix.Length..^".sql".Length], sql, sha256));
        }

        return migrations;
    }

    private static async Task<object?> ScalarAsync(NpgsqlConnection connection, string sql, CancellationToken ct)
    {
        await using var command = new NpgsqlCommand(sql, connection);
        return await command.ExecuteScalarAsync(ct);
    }

    private static async Task ExecuteAsync(
        NpgsqlConnection connection, string sql, CancellationToken ct, NpgsqlTransaction? transaction = null)
    {
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        await command.ExecuteNonQueryAsync(ct);
    }
}

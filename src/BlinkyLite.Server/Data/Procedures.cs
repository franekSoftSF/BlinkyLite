using System.Net;
using System.Text.Json;
using BlinkyLite.Contracts;
using Npgsql;
using NpgsqlTypes;

namespace BlinkyLite.Server.Data;

/// <summary>Who is asking; every bl_* function records it in the audit trail.</summary>
public sealed record Actor(string Upn, string? Sid, IReadOnlyCollection<Role> Roles, IPAddress? SourceIp);

/// <summary>
/// A bl_* function refused: SQLSTATE class BL, with a message key the client
/// translates (docs/07-database.md#błędy).
/// </summary>
public sealed class DatabaseRuleException(string sqlState, string messageKey, string? detail, Exception inner)
    : Exception($"{sqlState} {messageKey}: {detail}", inner)
{
    public string SqlState { get; } = sqlState;
    public string MessageKey { get; } = messageKey;
    public string? Detail { get; } = detail;
}

public sealed record IssuanceReservation(
    long CardSerial,
    string Firmware,
    bool HasPuk,
    string TargetSam,
    string TargetUpn,
    string TargetSid,
    string TargetDisplayName,
    string ProfileName,
    string TemplateName,
    string CaConfig,
    string WindowsIdentity,
    string Workstation,
    byte[]? PukEnvelope,
    byte[] MgmtKeyEnvelope,
    byte MgmtKeyAlgorithm,
    short KekVersion);

public sealed record AttestationRecord(
    byte[] AttestationDer,
    byte[] IntermediateDer,
    byte[] CsrDer,
    string KeyAlgorithm,
    short PinPolicy,
    short TouchPolicy,
    short? FormFactor);

public sealed record IssuedCertificate(
    byte[] CertificateDer,
    string SerialNumber,
    string Thumbprint,
    DateTime NotBefore,
    DateTime NotAfter);

public enum SecretKind
{
    Puk,
    ManagementKey,
}

public sealed record SecretEnvelope(Guid IssuanceId, byte[] Envelope, short KekVersion);

public sealed record ManagementKeyCandidate(
    Guid SecretId,
    Guid IssuanceId,
    CardSecretState State,
    byte[] Envelope,
    byte Algorithm,
    short KekVersion);

/// <summary>Events <c>bl_audit</c> accepts; everything else is written by the function that caused it.</summary>
public enum AuditAction
{
    AuthLogin,
    AuthDenied,
}

/// <summary>
/// The server's only write path: one typed method per bl_* function. Each call
/// is one statement and therefore one transaction - the function locks, checks,
/// changes and audits inside it, so there is nothing to coordinate here.
/// </summary>
/// <remarks>
/// Plain Npgsql rather than an NHibernate SQL query: inet, text[], jsonb and
/// bytea need explicit parameter types, which NHibernate would guess.
/// </remarks>
public sealed class Procedures(NpgsqlDataSource dataSource)
{
    public async Task<Guid> ReserveIssuanceAsync(IssuanceReservation r, Actor actor, CancellationToken ct = default)
    {
        var id = await ScalarAsync("bl_issuance_reserve",
        [
            Bigint(r.CardSerial), Text(r.Firmware), Bool(r.HasPuk),
            Text(r.TargetSam), Text(r.TargetUpn), Text(r.TargetSid), Text(r.TargetDisplayName),
            Text(r.ProfileName), Text(r.TemplateName), Text(r.CaConfig),
            Text(r.WindowsIdentity), Text(r.Workstation),
            Bytes(r.PukEnvelope), Bytes(r.MgmtKeyEnvelope), Smallint(r.MgmtKeyAlgorithm), Smallint(r.KekVersion),
        ], actor, ct);

        return (Guid)id!;
    }

    public Task MarkCustomisedAsync(Guid issuanceId, Actor actor, CancellationToken ct = default) =>
        ScalarAsync("bl_issuance_customised", [Uuid(issuanceId)], actor, ct);

    public Task MarkAttestedAsync(Guid issuanceId, AttestationRecord a, Actor actor, CancellationToken ct = default) =>
        ScalarAsync("bl_issuance_attested",
        [
            Uuid(issuanceId), Bytes(a.AttestationDer), Bytes(a.IntermediateDer), Bytes(a.CsrDer),
            Text(a.KeyAlgorithm), Smallint(a.PinPolicy), Smallint(a.TouchPolicy), Smallint(a.FormFactor),
        ], actor, ct);

    public Task MarkSubmittedAsync(Guid issuanceId, int caRequestId, string eaThumbprint, Actor actor, CancellationToken ct = default) =>
        ScalarAsync("bl_issuance_submitted", [Uuid(issuanceId), Integer(caRequestId), Text(eaThumbprint)], actor, ct);

    public Task MarkPendingAsync(Guid issuanceId, Actor actor, CancellationToken ct = default) =>
        ScalarAsync("bl_issuance_pending", [Uuid(issuanceId)], actor, ct);

    public Task MarkIssuedAsync(Guid issuanceId, IssuedCertificate c, Actor actor, CancellationToken ct = default) =>
        ScalarAsync("bl_issuance_issued",
        [
            Uuid(issuanceId), Bytes(c.CertificateDer), Text(c.SerialNumber), Text(c.Thumbprint),
            Timestamp(c.NotBefore), Timestamp(c.NotAfter),
        ], actor, ct);

    public Task MarkFailedAsync(Guid issuanceId, string error, Actor actor, CancellationToken ct = default) =>
        ScalarAsync("bl_issuance_failed", [Uuid(issuanceId), Text(error)], actor, ct);

    public async Task<SecretEnvelope> DiscloseSecretAsync(
        long cardSerial, SecretKind kind, string reason, Actor actor, CancellationToken ct = default)
    {
        var kindName = kind == SecretKind.Puk ? "puk" : "mgmt-key";
        var rows = await RowsAsync("bl_secret_disclose", [Bigint(cardSerial), Text(kindName), Text(reason)], actor,
            reader => new SecretEnvelope(reader.GetGuid(0), reader.GetFieldValue<byte[]>(1), reader.GetInt16(2)), ct);

        return rows.Single();
    }

    public Task<IReadOnlyList<ManagementKeyCandidate>> GetManagementKeyCandidatesAsync(
        long cardSerial, Actor actor, CancellationToken ct = default) =>
        RowsAsync("bl_mgmt_key_candidates", [Bigint(cardSerial)], actor,
            reader => new ManagementKeyCandidate(
                reader.GetGuid(0),
                reader.GetGuid(1),
                Enum.Parse<CardSecretState>(reader.GetString(2)),
                reader.GetFieldValue<byte[]>(3),
                checked((byte)reader.GetInt16(4)),
                reader.GetInt16(5)), ct);

    public Task AuditAsync(AuditAction action, object? data, Actor actor, CancellationToken ct = default)
    {
        var name = action switch
        {
            AuditAction.AuthLogin => "auth.login",
            AuditAction.AuthDenied => "auth.denied",
            _ => throw new ArgumentOutOfRangeException(nameof(action)),
        };

        return ScalarAsync("bl_audit", [Text(name), Json(data)], actor, ct);
    }

    private async Task<object?> ScalarAsync(string function, NpgsqlParameter[] args, Actor actor, CancellationToken ct)
    {
        await using var command = Build($"select {Migrations.Schema}.{function}", args, actor);
        try
        {
            return await command.ExecuteScalarAsync(ct);
        }
        catch (PostgresException e) when (IsRule(e))
        {
            throw Rule(e);
        }
    }

    private async Task<IReadOnlyList<T>> RowsAsync<T>(
        string function, NpgsqlParameter[] args, Actor actor, Func<NpgsqlDataReader, T> read, CancellationToken ct)
    {
        await using var command = Build($"select * from {Migrations.Schema}.{function}", args, actor);
        try
        {
            var rows = new List<T>();
            await using var reader = await command.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                rows.Add(read(reader));
            }

            return rows;
        }
        catch (PostgresException e) when (IsRule(e))
        {
            throw Rule(e);
        }
    }

    private NpgsqlCommand Build(string call, NpgsqlParameter[] args, Actor actor)
    {
        NpgsqlParameter[] all =
        [
            .. args,
            Text(actor.Upn),
            Text(actor.Sid),
            new() { NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Text, Value = actor.Roles.Select(r => r.ToString()).ToArray() },
            new() { NpgsqlDbType = NpgsqlDbType.Inet, Value = (object?)actor.SourceIp ?? DBNull.Value },
        ];

        var command = dataSource.CreateCommand(
            $"{call}({string.Join(", ", Enumerable.Range(1, all.Length).Select(i => $"${i}"))})");
        command.Parameters.AddRange(all);
        return command;
    }

    private static bool IsRule(PostgresException e) => e.SqlState.StartsWith("BL", StringComparison.Ordinal);

    private static DatabaseRuleException Rule(PostgresException e) => new(e.SqlState, e.MessageText, e.Detail, e);

    private static NpgsqlParameter Text(string? value) => Typed(NpgsqlDbType.Text, value);

    private static NpgsqlParameter Bigint(long value) => Typed(NpgsqlDbType.Bigint, value);

    private static NpgsqlParameter Integer(int value) => Typed(NpgsqlDbType.Integer, value);

    private static NpgsqlParameter Smallint(short? value) => Typed(NpgsqlDbType.Smallint, value);

    private static NpgsqlParameter Bool(bool value) => Typed(NpgsqlDbType.Boolean, value);

    private static NpgsqlParameter Uuid(Guid value) => Typed(NpgsqlDbType.Uuid, value);

    private static NpgsqlParameter Bytes(byte[]? value) => Typed(NpgsqlDbType.Bytea, value);

    private static NpgsqlParameter Json(object? value) =>
        Typed(NpgsqlDbType.Jsonb, value is null ? null : JsonSerializer.Serialize(value));

    private static NpgsqlParameter Timestamp(DateTime value)
    {
        // A local time would be shifted by the server's offset without anyone
        // noticing; refuse it here rather than store the wrong expiry date.
        if (value.Kind != DateTimeKind.Utc)
        {
            throw new ArgumentException($"Timestamps must be UTC, got {value.Kind}.", nameof(value));
        }

        return Typed(NpgsqlDbType.TimestampTz, value);
    }

    private static NpgsqlParameter Typed(NpgsqlDbType type, object? value) =>
        new() { NpgsqlDbType = type, Value = value ?? DBNull.Value };
}

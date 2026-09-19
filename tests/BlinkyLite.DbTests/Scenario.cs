using System.Net;
using System.Security.Cryptography;
using BlinkyLite.Contracts;
using BlinkyLite.Server.Data;
using Npgsql;

namespace BlinkyLite.DbTests;

/// <summary>Actors and ready-made issuances in any state, built only through the bl_* functions.</summary>
internal sealed class Scenario(DatabaseFixture db)
{
    public static readonly Actor Officer = new("so@corp.example", "S-1-5-21-100-200-300-1101", [Role.SecurityOfficer], IPAddress.Loopback);
    public static readonly Actor Admin = new("admin@corp.example", "S-1-5-21-100-200-300-1100", [Role.Admin], IPAddress.Loopback);
    public static readonly Actor Helpdesk = new("hd@corp.example", "S-1-5-21-100-200-300-1102", [Role.Helpdesk], IPAddress.IPv6Loopback);

    public Procedures Procedures => db.Procedures;

    public static IssuanceReservation Reservation(long serial, bool hasPuk = true) => new(
        IssuanceId: Guid.NewGuid(),
        CardSerial: serial,
        Firmware: "5.7.1",
        HasPuk: hasPuk,
        TargetSam: @"CORP\jkowalski",
        TargetUpn: "jkowalski@corp.example",
        TargetSid: "S-1-5-21-100-200-300-4242",
        TargetDisplayName: "Jan Kowalski",
        ProfileName: "SmartcardLogon",
        TemplateName: "BlinkyLiteSmartcardLogon",
        CaConfig: @"SUBCA\Corp Issuing CA",
        WindowsIdentity: @"CORP\adm-so",
        Workstation: "WS-042",
        PukEnvelope: hasPuk ? RandomNumberGenerator.GetBytes(48) : null,
        MgmtKeyEnvelope: RandomNumberGenerator.GetBytes(56),
        MgmtKeyAlgorithm: 0x0A,
        KekVersion: 1);

    public static AttestationRecord Attestation() => new(
        RandomNumberGenerator.GetBytes(600), RandomNumberGenerator.GetBytes(700), RandomNumberGenerator.GetBytes(500),
        "Rsa2048", PinPolicy: 2, TouchPolicy: 1, FormFactor: 3);

    public static IssuedCertificate Certificate() => new(
        RandomNumberGenerator.GetBytes(1019),
        Convert.ToHexString(RandomNumberGenerator.GetBytes(16)),
        Convert.ToHexString(RandomNumberGenerator.GetBytes(20)),
        DateTime.UtcNow.AddMinutes(-5),
        DateTime.UtcNow.AddYears(1));

    /// <summary>A new card with one issuance driven to <paramref name="state"/>.</summary>
    public async Task<(long Serial, Guid Id)> IssuanceIn(IssuanceState state)
    {
        var serial = db.NewSerial();
        return (serial, await IssuanceIn(state, serial));
    }

    public async Task<Guid> IssuanceIn(IssuanceState state, long serial)
    {
        if (state == IssuanceState.Superseded)
        {
            var first = await IssuanceIn(IssuanceState.Issued, serial);
            await IssuanceIn(IssuanceState.Issued, serial);
            return first;
        }

        var id = await Procedures.ReserveIssuanceAsync(Reservation(serial), Officer);
        if (state == IssuanceState.Reserved)
        {
            return id;
        }

        if (state == IssuanceState.Failed)
        {
            await Procedures.MarkFailedAsync(id, "test failure", Officer);
            return id;
        }

        await Procedures.MarkCustomisedAsync(id, Officer);
        if (state == IssuanceState.Customised)
        {
            return id;
        }

        await Procedures.MarkAttestedAsync(id, Attestation(), Officer);
        if (state == IssuanceState.Attested)
        {
            return id;
        }

        await Procedures.MarkSubmittedAsync(id, 7001, "AB12CD34", Officer);
        if (state == IssuanceState.PendingCa)
        {
            await Procedures.MarkPendingAsync(id, Officer);
            return id;
        }

        await Procedures.MarkIssuedAsync(id, Certificate(), Officer);
        return id;
    }

    public async Task<string> StateOf(Guid issuanceId) =>
        (string)(await Scalar("select state from blinkylite.issuances where id = $1", issuanceId))!;

    public async Task<List<string>> AuditActions(Guid issuanceId)
    {
        var actions = new List<string>();
        await using var command = db.AppDataSource.CreateCommand(
            "select action from blinkylite.audit_events where issuance_id = $1 order by id");
        command.Parameters.Add(new NpgsqlParameter { Value = issuanceId });
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            actions.Add(reader.GetString(0));
        }

        return actions;
    }

    public async Task<object?> Scalar(string sql, params object[] args)
    {
        await using var command = db.AppDataSource.CreateCommand(sql);
        foreach (var arg in args)
        {
            command.Parameters.Add(new NpgsqlParameter { Value = arg });
        }

        return await command.ExecuteScalarAsync();
    }
}

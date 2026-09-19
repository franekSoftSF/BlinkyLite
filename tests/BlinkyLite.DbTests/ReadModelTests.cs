using BlinkyLite.Contracts;
using BlinkyLite.Server.Data;
using NHibernate.Linq;

namespace BlinkyLite.DbTests;

[Collection(DatabaseCollection.Name)]
public sealed class ReadModelTests(DatabaseFixture db)
{
    private readonly Scenario scenario = new(db);

    [DbFact]
    public void The_mappings_match_the_migrated_schema_as_seen_by_the_application_role()
    {
        var result = ReadSessions.Validate(db.ReadConfiguration);

        Assert.True(result.IsValid, result.Summary);
    }

    [DbFact]
    public async Task Issuances_read_back_with_UTC_timestamps_and_filter_by_a_UTC_parameter()
    {
        var since = DateTime.UtcNow.AddMinutes(-1);
        var (serial, id) = await scenario.IssuanceIn(IssuanceState.Issued);

        using var session = db.Sessions.OpenReadOnlySession();
        var issuance = await session.Query<Issuance>()
            .Where(i => i.CardSerial == serial && i.CreatedAt > since)
            .SingleAsync();
        var card = await session.GetAsync<Card>(serial);

        Assert.Equal(id, issuance.Id);
        Assert.Equal(IssuanceState.Issued, issuance.State);
        Assert.Equal(DateTimeKind.Utc, issuance.CreatedAt.Kind);
        Assert.NotNull(issuance.CertificateDer);
        Assert.Equal(@"CORP\jkowalski", issuance.TargetSam);
        Assert.Equal(id, card.CurrentIssuanceId);
        Assert.Equal((short)3, card.FormFactor);
    }

    [DbFact]
    public async Task Envelope_bookkeeping_and_audit_events_are_readable_without_the_envelopes()
    {
        var (serial, id) = await scenario.IssuanceIn(IssuanceState.Customised);

        using var session = db.Sessions.OpenReadOnlySession();
        var secret = await session.Query<CardSecret>().SingleAsync(s => s.IssuanceId == id);
        var events = await session.Query<AuditEvent>()
            .Where(e => e.CardSerial == serial)
            .OrderBy(e => e.Id)
            .ToListAsync();

        Assert.Equal(CardSecretState.Active, secret.State);
        Assert.Equal((short)0x0A, secret.MgmtKeyAlgorithm);
        Assert.Equal(["issuance.reserved", "issuance.customised"], events.Select(e => e.Action));
        Assert.Equal("SecurityOfficer", events[0].ActorRoles);
        Assert.Equal("127.0.0.1", events[0].SourceIp);
        Assert.Contains("CORP", events[0].Data);
    }

    [DbFact]
    public async Task A_read_only_session_never_writes()
    {
        var (_, id) = await scenario.IssuanceIn(IssuanceState.Reserved);

        using var session = db.Sessions.OpenReadOnlySession();
        var issuance = await session.GetAsync<Issuance>(id);
        typeof(Issuance).GetProperty(nameof(Issuance.State))!.SetValue(issuance, IssuanceState.Issued);
        await session.FlushAsync();

        Assert.True(session.IsReadOnly(issuance));
        Assert.Equal("Reserved", await scenario.StateOf(id));
    }
}

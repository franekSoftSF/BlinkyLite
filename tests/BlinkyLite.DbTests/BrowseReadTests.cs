using BlinkyLite.Contracts;
using BlinkyLite.Server.Data;

namespace BlinkyLite.DbTests;

/// <summary>
/// The browser's reads (0030) on the real schema, as the application role:
/// the LINQ has to become SQL PostgreSQL accepts, and the audit read must not
/// need a column the role cannot see.
/// </summary>
[Collection(DatabaseCollection.Name)]
public sealed class BrowseReadTests(DatabaseFixture db)
{
    private readonly Scenario scenario = new(db);

    private IssuanceReader Reader => new(db.Sessions);

    [DbFact]
    public async Task The_list_finds_a_card_by_its_serial_and_by_part_of_a_name_in_any_case()
    {
        var (serial, id) = await scenario.IssuanceIn(IssuanceState.Issued);

        var bySerial = Reader.List(serial.ToString(System.Globalization.CultureInfo.InvariantCulture), 1, 50);
        var byName = Reader.List("KOWAL", 1, 500);

        Assert.Contains(bySerial.Items, i => i.Id == id);
        Assert.All(bySerial.Items, i => Assert.Equal(serial, i.CardSerial));
        Assert.Contains(byName.Items, i => i.Id == id);
    }

    [DbFact]
    public async Task The_list_is_newest_first_and_paged()
    {
        await scenario.IssuanceIn(IssuanceState.Issued);
        await scenario.IssuanceIn(IssuanceState.Failed);

        var first = Reader.List(null, 1, 1);
        var second = Reader.List(null, 2, 1);

        Assert.Single(first.Items);
        Assert.True(first.Total >= 2);
        Assert.True(first.Items[0].CreatedAt >= second.Items[0].CreatedAt);
        Assert.NotEqual(first.Items[0].Id, second.Items[0].Id);
    }

    [DbFact]
    public async Task A_card_its_secret_bookkeeping_and_its_audit_read_back()
    {
        var (serial, id) = await scenario.IssuanceIn(IssuanceState.Issued);
        await scenario.Procedures.DiscloseSecretAsync(serial, SecretKind.Puk, "INC-1 PIN blocked", Scenario.Helpdesk);

        var card = Reader.FindCard(serial);
        var secret = Reader.SecretOf(id);
        var audit = Reader.Audit(serial, 1, 50);

        Assert.Equal(id, card!.CurrentIssuanceId);
        Assert.Equal(1, secret!.PukDisclosedCount);
        Assert.Equal("puk.disclosed", audit.Items[0].Action);
        Assert.Equal("Helpdesk", audit.Items[0].ActorRoles);
        Assert.Contains("INC-1 PIN blocked", audit.Items[0].Data);
        Assert.All(audit.Items, a => Assert.Equal(serial, a.CardSerial));
    }
}

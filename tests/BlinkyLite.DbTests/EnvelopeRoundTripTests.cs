using System.Security.Cryptography;
using System.Text;
using BlinkyLite.Server.Data;
using BlinkyLite.Server.Secrets;

namespace BlinkyLite.DbTests;

/// <summary>
/// The envelopes and the database together: what bl_secret_disclose gives back
/// has to open, and only for the row it was sealed for.
/// </summary>
[Collection(DatabaseCollection.Name)]
public sealed class EnvelopeRoundTripTests(DatabaseFixture db)
{
    private readonly Scenario scenario = new(db);

    [DbFact]
    public async Task A_PUK_sealed_before_the_card_was_touched_opens_again_after_disclosure()
    {
        var envelopes = Envelopes();
        var serial = db.NewSerial();
        var issuanceId = Guid.NewGuid();
        var puk = Encoding.ASCII.GetBytes("48271930");
        var mgmtKey = RandomNumberGenerator.GetBytes(24);

        // The order the server keeps: seal, reserve, only then the card.
        var reservation = Scenario.Reservation(serial) with
        {
            IssuanceId = issuanceId,
            PukEnvelope = envelopes.Seal(puk, new EnvelopeBinding(EnvelopeKind.Puk, serial, issuanceId)),
            MgmtKeyEnvelope = envelopes.Seal(mgmtKey, new EnvelopeBinding(EnvelopeKind.ManagementKey, serial, issuanceId)),
            KekVersion = envelopes.CurrentVersion,
        };

        var id = await scenario.Procedures.ReserveIssuanceAsync(reservation, Scenario.Officer);
        Assert.Equal(issuanceId, id);

        await scenario.Procedures.MarkCustomisedAsync(id, Scenario.Officer);
        var disclosed = await scenario.Procedures.DiscloseSecretAsync(serial, SecretKind.Puk, "INC-9 unblock", Scenario.Helpdesk);

        Assert.Equal(issuanceId, disclosed.IssuanceId);
        Assert.Equal(envelopes.CurrentVersion, disclosed.KekVersion);
        Assert.Equal(puk, envelopes.Open(disclosed.Envelope, new EnvelopeBinding(EnvelopeKind.Puk, serial, disclosed.IssuanceId)));

        var mgmt = await scenario.Procedures.DiscloseSecretAsync(serial, SecretKind.ManagementKey, "INC-9 re-issue", Scenario.Admin);
        Assert.Equal(mgmtKey, envelopes.Open(mgmt.Envelope, new EnvelopeBinding(EnvelopeKind.ManagementKey, serial, mgmt.IssuanceId)));
    }

    [DbFact]
    public async Task An_envelope_from_another_issuance_of_the_same_card_does_not_open()
    {
        var envelopes = Envelopes();
        var serial = db.NewSerial();

        var first = await Reserve(envelopes, serial, "11111111");
        await scenario.Procedures.MarkCustomisedAsync(first.Id, Scenario.Officer);
        await scenario.Procedures.MarkFailedAsync(first.Id, "card left the room", Scenario.Officer);
        var second = await Reserve(envelopes, serial, "22222222");

        // Taking the first issuance's envelope and reading it as the second's
        // is exactly what the AAD is there to stop.
        Assert.ThrowsAny<CryptographicException>(() =>
            envelopes.Open(first.Envelope, new EnvelopeBinding(EnvelopeKind.Puk, serial, second.Id)));
        Assert.Equal(Encoding.ASCII.GetBytes("11111111"),
            envelopes.Open(first.Envelope, new EnvelopeBinding(EnvelopeKind.Puk, serial, first.Id)));
    }

    private async Task<(Guid Id, byte[] Envelope)> Reserve(SecretEnvelopes envelopes, long serial, string puk)
    {
        var id = Guid.NewGuid();
        var envelope = envelopes.Seal(Encoding.ASCII.GetBytes(puk), new EnvelopeBinding(EnvelopeKind.Puk, serial, id));
        var reservation = Scenario.Reservation(serial) with { IssuanceId = id, PukEnvelope = envelope };

        await scenario.Procedures.ReserveIssuanceAsync(reservation, Scenario.Officer);
        return (id, envelope);
    }

    private static SecretEnvelopes Envelopes()
    {
        var options = new KekOptions { CurrentKekVersion = 1 };
        options.Keks["1"] = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        return new SecretEnvelopes(options);
    }
}

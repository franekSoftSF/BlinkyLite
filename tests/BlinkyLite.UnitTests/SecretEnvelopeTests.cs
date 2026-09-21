using System.Security.Cryptography;
using System.Text;
using BlinkyLite.Server.Secrets;

namespace BlinkyLite.UnitTests;

public sealed class SecretEnvelopeTests
{
    private static readonly byte[] Puk = Encoding.ASCII.GetBytes("48271930");
    private static readonly EnvelopeBinding Binding =
        new(EnvelopeKind.Puk, 29051525, Guid.Parse("11111111-1111-1111-1111-111111111111"));

    [Fact]
    public void A_sealed_PUK_comes_back_for_its_own_card_and_issuance()
    {
        var envelopes = Envelopes((1, 0xA1));

        var envelope = envelopes.Seal(Puk, Binding);

        Assert.Equal(Puk, envelopes.Open(envelope, Binding));
        Assert.Equal(1, envelope[0]);
    }

    [Fact]
    public void A_TOTP_secret_opens_only_for_the_operator_it_was_sealed_for()
    {
        var envelopes = Envelopes((1, 0xA1));
        var secret = RandomNumberGenerator.GetBytes(20);

        var envelope = envelopes.SealTotp(secret, "S-1-5-21-1-2-3-500");

        Assert.Equal(secret, envelopes.OpenTotp(envelope, "S-1-5-21-1-2-3-500"));
        Assert.ThrowsAny<CryptographicException>(() => envelopes.OpenTotp(envelope, "S-1-5-21-1-2-3-501"));
    }

    [Fact]
    public void A_backup_code_hash_depends_on_the_KEK_and_the_operator()
    {
        var envelopes = Envelopes((1, 0xA1));
        var other = Envelopes((1, 0xB2));

        var hash = envelopes.BackupCodeHash(1, "S-1-5-21-1-2-3-500", "ABCDEFGHJK");

        Assert.Equal(32, hash.Length);
        Assert.Equal(hash, envelopes.BackupCodeHash(1, "S-1-5-21-1-2-3-500", "ABCDEFGHJK"));
        Assert.NotEqual(hash, envelopes.BackupCodeHash(1, "S-1-5-21-1-2-3-501", "ABCDEFGHJK"));
        Assert.NotEqual(hash, other.BackupCodeHash(1, "S-1-5-21-1-2-3-500", "ABCDEFGHJK"));
    }

    public static TheoryData<string, EnvelopeBinding> OtherRows() => new()
    {
        { "another issuance of the same card", Binding with { IssuanceId = Guid.Parse("22222222-2222-2222-2222-222222222222") } },
        { "another card", Binding with { CardSerial = 29051526 } },
        { "the management key column of the same row", Binding with { Kind = EnvelopeKind.ManagementKey } },
    };

    [Theory]
    [MemberData(nameof(OtherRows))]
    public void An_envelope_moved_to_another_row_does_not_open(string row, EnvelopeBinding other)
    {
        var envelopes = Envelopes((1, 0xA1));
        var envelope = envelopes.Seal(Puk, Binding);

        Assert.ThrowsAny<CryptographicException>(() => envelopes.Open(envelope, other));
        Assert.NotEmpty(row);
    }

    [Fact]
    public void A_changed_byte_anywhere_in_the_envelope_is_caught()
    {
        var envelopes = Envelopes((1, 0xA1));
        var envelope = envelopes.Seal(Puk, Binding);

        for (var i = 0; i < envelope.Length; i++)
        {
            var tampered = envelope.ToArray();
            tampered[i] ^= 0xFF;

            Assert.ThrowsAny<CryptographicException>(() => envelopes.Open(tampered, Binding));
        }
    }

    [Fact]
    public void After_a_KEK_rotation_old_envelopes_still_open_and_new_ones_use_the_new_version()
    {
        var before = Envelopes((1, 0xA1));
        var old = before.Seal(Puk, Binding);

        var after = Envelopes((2, 0xB2), (1, 0xA1));
        var fresh = after.Seal(Puk, Binding);

        Assert.Equal(Puk, after.Open(old, Binding));
        Assert.Equal(1, old[2]);
        Assert.Equal(2, fresh[2]);
        Assert.Equal(2, after.CurrentVersion);
    }

    [Fact]
    public void An_envelope_whose_KEK_is_gone_says_which_version_is_missing()
    {
        var envelope = Envelopes((1, 0xA1)).Seal(Puk, Binding);
        var withoutVersionOne = Envelopes((2, 0xB2));

        var error = Assert.Throws<CryptographicException>(() => withoutVersionOne.Open(envelope, Binding));

        Assert.Contains("version 1", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Two_envelopes_of_the_same_secret_never_look_alike()
    {
        var envelopes = Envelopes((1, 0xA1));

        Assert.NotEqual(envelopes.Seal(Puk, Binding), envelopes.Seal(Puk, Binding));
    }

    [Theory]
    [InlineData("not base64")]
    [InlineData("YWJj")]
    public void A_KEK_that_is_not_32_bytes_of_base64_stops_the_server(string kek)
    {
        var options = new KekOptions { CurrentKekVersion = 1 };
        options.Keks["1"] = kek;

        Assert.Throws<InvalidOperationException>(() => new SecretEnvelopes(options));
    }

    [Fact]
    public void A_current_version_without_a_key_stops_the_server()
    {
        var options = new KekOptions { CurrentKekVersion = 3 };
        options.Keks["1"] = Convert.ToBase64String(new byte[32]);

        Assert.Throws<InvalidOperationException>(() => new SecretEnvelopes(options));
    }

    private static SecretEnvelopes Envelopes(params (short Version, byte Fill)[] keks)
    {
        var options = new KekOptions { CurrentKekVersion = keks[0].Version };
        foreach (var (version, fill) in keks)
        {
            options.Keks[version.ToString()] = Convert.ToBase64String(Enumerable.Repeat(fill, 32).ToArray());
        }

        return new SecretEnvelopes(options);
    }
}

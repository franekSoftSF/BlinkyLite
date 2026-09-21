using System.Text;
using BlinkyLite.Server.Auth;

namespace BlinkyLite.UnitTests;

public sealed class TotpTests
{
    // RFC 6238 appendix B, SHA-1. The RFC prints eight digits; six are the
    // same number modulo 10^6, which is what authenticator apps show.
    private static readonly byte[] RfcSecret = Encoding.ASCII.GetBytes("12345678901234567890");

    [Theory]
    [InlineData(59L, "94287082")]
    [InlineData(1111111109L, "07081804")]
    [InlineData(1111111111L, "14050471")]
    [InlineData(1234567890L, "89005924")]
    [InlineData(2000000000L, "69279037")]
    [InlineData(20000000000L, "65353130")]
    public void The_codes_are_the_RFC_6238_test_vectors(long unixTime, string expected)
    {
        var step = Totp.StepAt(DateTimeOffset.FromUnixTimeSeconds(unixTime));

        Assert.Equal(expected, Totp.Code(RfcSecret, step, digits: 8));
        Assert.Equal(expected[2..], Totp.Code(RfcSecret, step));
    }

    [Theory]
    [InlineData("", "")]
    [InlineData("f", "MY")]
    [InlineData("fo", "MZXQ")]
    [InlineData("foo", "MZXW6")]
    [InlineData("foob", "MZXW6YQ")]
    [InlineData("fooba", "MZXW6YTB")]
    [InlineData("foobar", "MZXW6YTBOI")]
    public void Base32_is_RFC_4648_without_padding(string input, string expected)
    {
        Assert.Equal(expected, Base32.Encode(Encoding.ASCII.GetBytes(input)));
        Assert.Equal(Encoding.ASCII.GetBytes(input), ServerFactory.Base32Decode(expected));
    }

    [Theory]
    [InlineData(-1, true)]
    [InlineData(0, true)]
    [InlineData(1, true)]
    [InlineData(-2, false)]
    [InlineData(2, false)]
    public void A_code_is_accepted_one_step_either_side_and_no_further(int offset, bool accepted)
    {
        var now = DateTimeOffset.FromUnixTimeSeconds(1_800_000_015);
        var step = Totp.StepAt(now) + offset;

        var matched = Totp.Match(RfcSecret, Totp.Code(RfcSecret, step), now);

        Assert.Equal(accepted ? step : null, matched);
    }

    [Theory]
    [InlineData("12345")]
    [InlineData("1234567")]
    [InlineData("12a456")]
    [InlineData("١٢٣٤٥٦")]
    public void Anything_but_six_ASCII_digits_is_not_a_code(string typed)
    {
        Assert.Null(Totp.Match(RfcSecret, typed, DateTimeOffset.UtcNow));
    }

    [Fact]
    public void The_otpauth_uri_names_the_issuer_twice_and_the_parameters_apps_read()
    {
        var uri = Totp.OtpAuthUri("BlinkyLite", "jan kowalski@corp.example", Encoding.ASCII.GetBytes("foobar"));

        Assert.Equal(
            "otpauth://totp/BlinkyLite:jan%20kowalski%40corp.example?secret=MZXW6YTBOI&issuer=BlinkyLite&algorithm=SHA1&digits=6&period=30",
            uri);
    }

    [Fact]
    public void Backup_codes_are_ten_distinct_codes_without_letters_people_confuse()
    {
        var codes = BackupCodes.Generate();

        Assert.Equal(BackupCodes.Count, codes.Distinct().Count());
        Assert.All(codes, c =>
        {
            Assert.Matches("^[A-Z2-9]{5}-[A-Z2-9]{5}$", c);
            Assert.DoesNotContain(c, ch => ch is '0' or 'O' or '1' or 'I' or 'L');
        });
    }

    [Theory]
    [InlineData("ABCDE-FGHJK", "ABCDEFGHJK")]
    [InlineData("abcde fghjk", "ABCDEFGHJK")]
    [InlineData(" abcdefghjk ", "ABCDEFGHJK")]
    [InlineData("ABCDE-FGHJ", null)]
    [InlineData("ABCDE-FGHIK", null)]
    [InlineData("123456", null)]
    public void A_backup_code_is_recognised_however_it_was_typed(string typed, string? normalised)
    {
        Assert.Equal(normalised, BackupCodes.Normalise(typed));
    }
}

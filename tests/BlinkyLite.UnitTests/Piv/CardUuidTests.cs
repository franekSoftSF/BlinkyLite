using BlinkyLite.Piv;

namespace BlinkyLite.UnitTests;

/// <summary>
/// The CHUID's GUID is a UUID, not sixteen random bytes (SP 800-73-5 Part 1,
/// 3.4.1). Card 39721373 went out with one that was neither version 1, 4 nor
/// 5 in the RFC 4122 sense.
/// </summary>
public sealed class CardUuidTests
{
    [Fact]
    public void Every_card_uuid_is_version_4_with_the_RFC_4122_variant()
    {
        // A thousand, because the bug this replaced produced a valid-looking
        // UUID by chance one time in sixty-four: a single sample would pass
        // the old code often enough to be worthless.
        for (var i = 0; i < 1000; i++)
        {
            var uuid = PivCardObjects.CardUuid();

            Assert.Equal(16, uuid.Length);
            Assert.Equal(0x40, uuid[6] & 0xF0);
            Assert.Equal(0x80, uuid[8] & 0xC0);
        }
    }

    [Fact]
    public void The_CHUID_carries_it_under_tag_34()
    {
        var chuid = PivCardObjects.BuildChuid(new DateOnly(2036, 9, 20));

        var at = Array.IndexOf(chuid, (byte)0x34);
        Assert.Equal(0x10, chuid[at + 1]);

        var guid = chuid.AsSpan(at + 2, 16);
        Assert.Equal(0x40, guid[6] & 0xF0);
        Assert.Equal(0x80, guid[8] & 0xC0);
    }
}

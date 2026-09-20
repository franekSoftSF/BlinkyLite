using BlinkyLite.Issuance;
using BlinkyLite.Piv;

namespace BlinkyLite.UnitTests;

/// <summary>
/// The definition of done for 0011 written as code: a card that answers all of
/// this is personalised, and one that misses any of it is not, whatever the
/// steps printed on the way.
/// </summary>
public sealed class PersonalisationChecksTests
{
    private static PersonalisationChecks Good() => new(
        ManagementKeyReadsBack: true,
        ManagementKeyBehindPin: true,
        RequestSignatureValid: true,
        Pin: PinState.Set,
        Puk: PinState.Set,
        SlotAlgorithm: PivAlgorithm.Rsa2048,
        SlotPinPolicy: PinPolicy.Once,
        SlotTouchPolicy: TouchPolicy.Never,
        SlotOrigin: KeyOrigin.Generated);

    [Fact]
    public void A_card_that_answers_everything_passes() => Assert.True(Good().Passed);

    public static TheoryData<string, PersonalisationChecks> Failures() => new()
    {
        { "management key not in PRINTED", Good() with { ManagementKeyReadsBack = false } },
        { "management key not behind the PIN", Good() with { ManagementKeyBehindPin = false } },
        { "the card did not sign the request", Good() with { RequestSignatureValid = false } },
        { "PIN still factory", Good() with { Pin = PinState.Default } },
        { "PUK still factory", Good() with { Puk = PinState.Default } },
        { "PIN blocked", Good() with { Pin = PinState.Blocked } },
        // An imported key would mean the private key existed somewhere else
        // first, which is the one thing attestation is for.
        { "key imported, not generated", Good() with { SlotOrigin = KeyOrigin.Imported } },
    };

    [Theory]
    [MemberData(nameof(Failures))]
    public void A_card_that_misses_one_thing_does_not_pass(string what, PersonalisationChecks checks)
    {
        Assert.False(checks.Passed, what);
    }
}

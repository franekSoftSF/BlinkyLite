using System.Globalization;

namespace BlinkyLite.Contracts;

/// <summary>What counts as an acceptable PIN.</summary>
/// <remarks>
/// The policy travels; the PIN does not. A PIN never leaves the workstation -
/// not to be checked, not to be stored, not ever - so the rule cannot be
/// applied where the rule is kept. Brought over from Blinky, with the
/// explanations replaced by message keys: BlinkyLite speaks four languages and
/// the sentence belongs in Messages.resx (docs/08).
/// </remarks>
public sealed record PinComplexityPolicy(
    int MinimumLength = 6,
    int MaximumLength = 8,
    bool DigitsOnly = true,
    bool ForbidDefault = true,
    bool ForbidRepeatedDigit = true,
    bool ForbidSequence = true,
    bool ForbidTokenSerial = true)
{
    public static PinComplexityPolicy Default { get; } = new();

    /// <summary>
    /// The PIN every PIV token leaves the factory with, and therefore the most
    /// common PIN on any fleet that has not been personalised.
    /// </summary>
    public const string FactoryPin = "123456";

    /// <summary>The PUK every PIV token leaves the factory with.</summary>
    public const string FactoryPuk = "12345678";
}

/// <summary>Why a PIN was refused, or that it was not.</summary>
public enum PinRefusal
{
    None,
    TooShort,
    TooLong,
    NotDigits,
    FactoryDefault,
    RepeatedDigit,
    Sequence,
    SameAsPuk,
    TokenSerial,
}

/// <summary>The answer and the key of the sentence to show, never the sentence itself.</summary>
public sealed record PinVerdict(PinRefusal Refusal, string MessageKey)
{
    public bool IsAcceptable => Refusal == PinRefusal.None;

    public static PinVerdict Acceptable { get; } = new(PinRefusal.None, "pin.rule.ok");
}

/// <summary>
/// Applies a <see cref="PinComplexityPolicy"/>: in the window while somebody
/// types, and again in the issuance engine before anything reaches the card.
/// </summary>
/// <remarks>
/// In both places on purpose. The window is where the rule gets explained; the
/// engine is where it gets enforced, and a rule checked only in the window is a
/// rule the PowerShell module does not have. These rules catch a PIN that is
/// obviously bad. They cannot catch a birthday, a name, or a PIN reused from
/// somewhere else.
/// </remarks>
public static class PinRules
{
    /// <param name="puk">
    /// Supplied only where it is already in hand. A PIN equal to the PUK means
    /// unblocking restores the value that was just rejected.
    /// </param>
    public static PinVerdict Check(string? pin, PinComplexityPolicy policy, long? tokenSerial = null, string? puk = null)
    {
        pin ??= string.Empty;

        if (pin.Length < policy.MinimumLength || pin.Length > policy.MaximumLength)
        {
            // Not only a policy choice: PIV carries the PIN in eight bytes.
            return new PinVerdict(
                pin.Length < policy.MinimumLength ? PinRefusal.TooShort : PinRefusal.TooLong,
                "pin.rule.length");
        }

        if (policy.DigitsOnly && !pin.All(char.IsAsciiDigit))
        {
            // A card accepts more, but software that reads the card later often
            // does not.
            return new PinVerdict(PinRefusal.NotDigits, "pin.rule.digits");
        }

        if (policy.ForbidDefault && pin == PinComplexityPolicy.FactoryPin)
        {
            return new PinVerdict(PinRefusal.FactoryDefault, "pin.rule.too-simple");
        }

        if (policy.ForbidRepeatedDigit && pin.Distinct().Count() == 1)
        {
            return new PinVerdict(PinRefusal.RepeatedDigit, "pin.rule.too-simple");
        }

        if (policy.ForbidSequence && IsRun(pin))
        {
            return new PinVerdict(PinRefusal.Sequence, "pin.rule.too-simple");
        }

        if (puk is not null && pin == puk)
        {
            return new PinVerdict(PinRefusal.SameAsPuk, "pin.rule.same-as-puk");
        }

        if (policy.ForbidTokenSerial && tokenSerial is { } serial
            && serial.ToString(CultureInfo.InvariantCulture).Contains(pin, StringComparison.Ordinal))
        {
            // The serial is printed on the object the PIN protects.
            return new PinVerdict(PinRefusal.TokenSerial, "pin.rule.too-simple");
        }

        return PinVerdict.Acceptable;
    }

    /// <summary>Ascending or descending by one, in either direction.</summary>
    private static bool IsRun(string pin)
    {
        if (pin.Length < 2 || !pin.All(char.IsAsciiDigit))
        {
            return false;
        }

        var step = pin[1] - pin[0];
        if (step is not (1 or -1))
        {
            return false;
        }

        for (var i = 2; i < pin.Length; i++)
        {
            if (pin[i] - pin[i - 1] != step)
            {
                return false;
            }
        }

        return true;
    }
}

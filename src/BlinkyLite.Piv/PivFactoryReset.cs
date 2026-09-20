namespace BlinkyLite.Piv;

/// <summary>What a reset had to do to get the card to accept it.</summary>
public sealed record FactoryResetReport(int PinAttemptsSpent, int PukAttemptsSpent)
{
    public override string ToString() =>
        $"PIN blocked after {PinAttemptsSpent} attempts, PUK after {PukAttemptsSpent}";
}

/// <summary>
/// Returning a token to its factory PIV state, destroying everything in it.
/// </summary>
/// <remarks>
/// <para>
/// In its own file because it is the one operation here that exists to lose
/// data. Every key in the PIV applet, every certificate and both secrets go;
/// nothing about it is recoverable, and no amount of care afterwards helps.
/// </para>
/// <para>
/// The card will not take <c>RESET</c> until the PIN <b>and</b> the PUK are
/// blocked - the applet's own safety catch, so that a lost card cannot be
/// wiped and reused by whoever found it. So this deliberately spends every
/// attempt on both, with values chosen not to be anybody's PIN, and only then
/// resets. That is what <c>ykman piv reset</c> does too, and it is why a reset
/// on a card whose PIN somebody still knows is not a quiet operation: it ends
/// with both counters at zero either way.
/// </para>
/// <para>
/// BlinkyLite issues and verifies; the rest of a card's life belongs to
/// Blinky. This is the exception the owner asked for, for the bench: a test
/// token has to become factory again between runs, and reaching for another
/// tool to do it is how a lab card ends up half-provisioned (D-22).
/// </para>
/// </remarks>
public partial class PivSession
{
    private const byte InsReset = 0xFB;

    /// <summary>
    /// Values used to burn the attempt counters. Eight characters, so they fit
    /// both a PIN and a PUK, and nothing anybody would set: if one of these
    /// ever happened to be right, the card would simply not block and the
    /// reset would refuse rather than do something unexpected.
    /// </summary>
    private const string BurnValue = "!!!!!!!!";

    /// <summary>
    /// Blocks the PIN and the PUK, then resets the applet to factory.
    /// </summary>
    /// <remarks>
    /// There is no confirmation here and there is no undo anywhere. Whatever
    /// asks for this has already decided.
    /// </remarks>
    public FactoryResetReport FactoryReset()
    {
        var pin = Burn("PIN", () => Connection.Send(
            new ApduCommand(InsVerify, p2: PinSlot, data: Padded(BurnValue))));

        // The PUK is burned with CHANGE REFERENCE DATA and not with VERIFY:
        // VERIFY is defined for the PIN, and a card asked to verify slot 81
        // may answer 6A88 rather than spending an attempt - which would loop
        // forever without ever blocking anything.
        var puk = Burn("PUK", () => Connection.Send(
            new ApduCommand(InsChangeReferenceData, p2: PukSlot,
                data: Padded(BurnValue, twice: true))));

        var response = Connection.Send(new ApduCommand(InsReset));

        if (response.Status.Value == StatusWord.SecurityStatusNotSatisfied)
        {
            // 6982 here means one of the counters is not at zero, which on a
            // YubiKey means the card was not in the state this believed.
            throw new PivAuthenticationFailedException(
                "RESET was refused: the card does not consider both the PIN and the PUK blocked.");
        }

        PivStatus.ThrowIfFailed(response.Status, "RESET");

        return new FactoryResetReport(pin, puk);
    }

    /// <summary>
    /// Sends one wrong answer at a time until the card says the counter is
    /// empty, and reports how many that took.
    /// </summary>
    /// <remarks>
    /// Driven by what the card answers rather than by a count read beforehand:
    /// a token whose PIN retries were changed has a different number, and a
    /// fixed loop would either stop early or keep talking to a card that is
    /// already blocked.
    /// </remarks>
    private static int Burn(string what, Func<ApduResponse> attempt)
    {
        var spent = 0;

        while (true)
        {
            var status = attempt().Status;

            // 6983: nothing left to spend, which is the state RESET wants.
            if (status.Value == StatusWord.AuthenticationMethodBlocked)
            {
                return spent;
            }

            // A success would mean the burn value really is this card's value.
            // Nothing sane follows from carrying on.
            if (status.IsSuccess)
            {
                throw new PivException(status, $"blocking the {what}",
                    $"the card accepted the value used to block the {what}; refusing to continue");
            }

            if (status.RetriesLeft is null)
            {
                throw new PivException(status, $"blocking the {what}",
                    $"unexpected answer while blocking the {what}");
            }

            spent++;

            if (spent > 32)
            {
                throw new PivException(status, $"blocking the {what}",
                    $"the {what} did not block after {spent} attempts");
            }
        }
    }

    /// <summary>
    /// The value padded to eight bytes with FF, as PIV wants it - twice over
    /// for the commands that take a current value and a new one.
    /// </summary>
    private static byte[] Padded(string value, bool twice = false)
    {
        var data = new byte[twice ? 16 : 8];
        Array.Fill(data, (byte)0xFF);

        for (var i = 0; i < value.Length && i < 8; i++)
        {
            data[i] = (byte)value[i];

            if (twice)
            {
                data[i + 8] = (byte)value[i];
            }
        }

        return data;
    }
}

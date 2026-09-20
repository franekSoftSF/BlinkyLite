namespace BlinkyLite.Piv;

public partial class PivSession
{
    /// <summary>
    /// True when ADMIN DATA carries the flag saying the management key is kept
    /// in PRINTED, behind the PIN.
    /// </summary>
    /// <remarks>
    /// This is the bit Yubico's own tools read: it is why <c>ykman piv info</c>
    /// prints "Management key is stored on the YubiKey, protected by PIN", and
    /// why the minidriver leaves such a card alone instead of taking it over.
    /// Reading it needs no PIN - the flag is a statement about the card, not a
    /// secret.
    /// </remarks>
    public bool IsManagementKeyBehindPin()
    {
        var request = new List<byte> { 0x5C, (byte)PivCardObjects.AdminData.Length };
        request.AddRange(PivCardObjects.AdminData);

        var response = Connection.Send(new ApduCommand(InsGetData, p1: 0x3F, p2: 0xFF, data: request.ToArray(), le: 0));

        // 6A82 means the object is simply not there, which is the answer on a
        // card nobody has personalised.
        if (!response.IsSuccess || response.Data.Length == 0)
        {
            return false;
        }

        var outer = Tlv.ParseBer(response.Data);
        var unwrapped = outer.TryGetValue(0x53, out var wrapped) ? Tlv.ParseBer(wrapped) : outer;

        if (!unwrapped.TryGetValue(PivCardObjects.AdminDataWrapper, out var admin))
        {
            return false;
        }

        return Tlv.ParseBer(admin).TryGetValue(PivCardObjects.AdminFlagsTag, out var flags)
            && flags.Length > 0
            && (flags[0] & PivCardObjects.AdminFlagManagementKeyProtected) != 0;
    }
}

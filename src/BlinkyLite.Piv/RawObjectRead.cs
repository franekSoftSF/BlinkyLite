namespace BlinkyLite.Piv;

/// <summary>A data object as the card returned it, or why it did not.</summary>
public sealed record RawObject(byte[] Tag, ushort Status, byte[] Data)
{
    public bool Present => Status == 0x9000 && Data.Length > 0;
}

public partial class PivSession
{
    /// <summary>
    /// Reads one data object and returns it exactly as the card sent it,
    /// including when the card refused.
    /// </summary>
    /// <remarks>
    /// For comparing two cards byte by byte: a card Windows logs in with and a
    /// card it does not. Every other read in this layer interprets what comes
    /// back - which is right for issuing and wrong here, because the
    /// difference being looked for may be exactly in the part an interpreter
    /// throws away. Only objects that need no PIN are worth asking for this
    /// way; PRINTED answers 6982 without one, and that answer is recorded too.
    /// </remarks>
    public RawObject ReadRawObject(byte[] tag)
    {
        var request = new List<byte> { 0x5C, (byte)tag.Length };
        request.AddRange(tag);

        var response = Connection.Send(new ApduCommand(InsGetData, p1: 0x3F, p2: 0xFF, data: request.ToArray(), le: 0));

        return new RawObject(tag, response.Status.Value, response.Data);
    }
}

using System.Security.Cryptography;
using System.Text;

namespace BlinkyLite.Server.Auth;

/// <summary>
/// RFC 6238 with the parameters every authenticator app understands: HMAC-SHA1,
/// six digits, thirty seconds.
/// </summary>
/// <remarks>
/// <para>
/// Written here rather than taken from Otp.NET, which winch uses: the whole
/// algorithm is an HMAC and a modulo, the RFC's own test vectors pin it down
/// (TotpTests), and a package for twenty lines would be one more thing in the
/// server that signs people in to check for a licence and for updates.
/// </para>
/// <para>
/// SHA-1 is not a weakness here: an HMAC does not rely on collision
/// resistance, and SHA-256 codes are silently wrong in several popular apps
/// that ignore the <c>algorithm</c> parameter.
/// </para>
/// </remarks>
public static class Totp
{
    public const int Digits = 6;
    public const int SecretBytes = 20;
    public static readonly TimeSpan Period = TimeSpan.FromSeconds(30);

    /// <summary>
    /// One step either side. A phone a minute off still works; wider would
    /// multiply the codes an attacker can guess at once for little gain.
    /// </summary>
    public const int Tolerance = 1;

    public static long StepAt(DateTimeOffset time) => time.ToUnixTimeSeconds() / (long)Period.TotalSeconds;

    public static string Code(ReadOnlySpan<byte> secret, long step, int digits = Digits)
    {
        Span<byte> counter = stackalloc byte[8];
        System.Buffers.Binary.BinaryPrimitives.WriteInt64BigEndian(counter, step);

        Span<byte> hash = stackalloc byte[20];
        HMACSHA1.HashData(secret, counter, hash);

        var offset = hash[^1] & 0x0F;
        var binary = ((hash[offset] & 0x7F) << 24) | (hash[offset + 1] << 16) | (hash[offset + 2] << 8) | hash[offset + 3];
        var modulo = (int)Math.Pow(10, digits);

        return (binary % modulo).ToString(System.Globalization.CultureInfo.InvariantCulture).PadLeft(digits, '0');
    }

    /// <summary>
    /// The step the code belongs to, or null. The caller still has to make
    /// sure that step has not been used (bl_totp_accept).
    /// </summary>
    public static long? Match(ReadOnlySpan<byte> secret, string code, DateTimeOffset now)
    {
        if (code.Length != Digits || !code.All(char.IsAsciiDigit))
        {
            return null;
        }

        var current = StepAt(now);
        long? found = null;

        // Every candidate is computed and compared in fixed time, so that the
        // answer does not tell how close a guess was or which step it hit.
        for (var step = current - Tolerance; step <= current + Tolerance; step++)
        {
            var expected = Encoding.ASCII.GetBytes(Code(secret, step));
            if (CryptographicOperations.FixedTimeEquals(expected, Encoding.ASCII.GetBytes(code)))
            {
                found ??= step;
            }
        }

        return found;
    }

    /// <summary>What goes into the QR code: the format Google Authenticator defined and everybody reads.</summary>
    public static string OtpAuthUri(string issuer, string account, ReadOnlySpan<byte> secret) =>
        $"otpauth://totp/{Uri.EscapeDataString(issuer)}:{Uri.EscapeDataString(account)}" +
        $"?secret={Base32.Encode(secret)}&issuer={Uri.EscapeDataString(issuer)}" +
        $"&algorithm=SHA1&digits={Digits}&period={(int)Period.TotalSeconds}";
}

/// <summary>RFC 4648 base32 without padding - the alphabet authenticator apps expect.</summary>
public static class Base32
{
    private const string Alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";

    public static string Encode(ReadOnlySpan<byte> data)
    {
        var output = new StringBuilder((data.Length * 8 + 4) / 5);
        int buffer = 0, bits = 0;

        foreach (var b in data)
        {
            buffer = (buffer << 8) | b;
            bits += 8;
            while (bits >= 5)
            {
                output.Append(Alphabet[(buffer >> (bits - 5)) & 31]);
                bits -= 5;
            }
        }

        if (bits > 0)
        {
            output.Append(Alphabet[(buffer << (5 - bits)) & 31]);
        }

        return output.ToString();
    }
}

/// <summary>
/// Single-use codes for the day the phone is lost: ten of them, shown once,
/// stored only as HMACs.
/// </summary>
/// <remarks>
/// Ten characters from 31 without 0/O and 1/I/L - just under fifty bits, read
/// aloud over a phone without spelling. Fifty bits is too few for a plain
/// hash in a database dump, which is why the stored value is keyed with the KEK
/// (<see cref="Secrets.SecretEnvelopes.BackupCodeHash"/>).
/// </remarks>
public static class BackupCodes
{
    public const int Count = 10;
    private const string Alphabet = "ABCDEFGHJKMNPQRSTUVWXYZ23456789";

    public static IReadOnlyList<string> Generate()
    {
        var codes = new List<string>(Count);
        while (codes.Count < Count)
        {
            var code = RandomNumberGenerator.GetString(Alphabet, 10);
            var formatted = $"{code[..5]}-{code[5..]}";
            if (!codes.Contains(formatted))
            {
                codes.Add(formatted);
            }
        }

        return codes;
    }

    /// <summary>
    /// What is hashed: upper case, no dash, no spaces - people type
    /// <c>abcde fghij</c> as often as <c>ABCDE-FGHIJ</c>. Null when it cannot be one.
    /// </summary>
    public static string? Normalise(string typed)
    {
        var letters = new string(typed.Where(c => c is not ('-' or ' ')).Select(char.ToUpperInvariant).ToArray());

        return letters.Length == 10 && letters.All(Alphabet.Contains) ? letters : null;
    }
}

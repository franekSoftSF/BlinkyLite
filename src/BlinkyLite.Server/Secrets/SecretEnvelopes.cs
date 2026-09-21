using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace BlinkyLite.Server.Secrets;

public enum EnvelopeKind
{
    Puk,
    ManagementKey,
}

/// <summary>Which card and which issuance an envelope belongs to; all of it goes into the AAD.</summary>
public readonly record struct EnvelopeBinding(EnvelopeKind Kind, long CardSerial, Guid IssuanceId)
{
    public string KindName => Kind == EnvelopeKind.Puk ? "puk" : "mgmt-key";
}

public sealed class KekOptions
{
    public const string Section = "Secrets";

    /// <summary>Version to seal new envelopes with.</summary>
    public short CurrentKekVersion { get; set; } = 1;

    /// <summary>
    /// Version number to base64 key (32 bytes, uniformly random). Old versions
    /// stay here for as long as any envelope uses them - removing one makes
    /// those PUKs unreadable, which is the point of keeping the KEK apart from
    /// the database and also the risk of losing it.
    /// </summary>
    public Dictionary<string, string> Keks { get; set; } = [];
}

/// <summary>
/// AES-256-GCM envelopes for PUKs, management keys and operators' TOTP secrets
/// (docs/03-data-model.md):
/// <code>
/// envelope = format(1) ‖ kek_version(2, big-endian) ‖ nonce(12) ‖ ciphertext ‖ tag(16)
/// key      = HKDF-Expand(KEK_v, "blinkylite/secret/v1|{kind}|{serial}|{hex nonce}", 32)
/// AAD      = "{kind}|{serial}|{issuance_id}"
/// </code>
/// A key per envelope and an AAD naming the card and the issuance come from
/// Blinky's PukEscrow: an envelope copied into another row fails
/// authentication instead of quietly handing out somebody else's PUK.
/// </summary>
public sealed class SecretEnvelopes
{
    private const byte Format = 1;
    private const int NonceSize = 12;
    private const int TagSize = 16;
    private const int HeaderSize = 1 + 2 + NonceSize;

    private readonly Dictionary<short, byte[]> keks;

    public SecretEnvelopes(KekOptions options)
    {
        keks = new Dictionary<short, byte[]>();
        foreach (var (version, value) in options.Keks)
        {
            if (!short.TryParse(version, out var number) || number <= 0)
            {
                throw new InvalidOperationException($"Secrets:Keks has a version '{version}' that is not a positive number.");
            }

            byte[] key;
            try
            {
                key = Convert.FromBase64String(value);
            }
            catch (FormatException)
            {
                throw new InvalidOperationException($"Secrets:Keks:{version} is not base64.");
            }

            if (key.Length != 32)
            {
                throw new InvalidOperationException($"Secrets:Keks:{version} is {key.Length} bytes; a KEK is 32.");
            }

            keks[number] = key;
        }

        CurrentVersion = options.CurrentKekVersion;
        if (!keks.ContainsKey(CurrentVersion))
        {
            throw new InvalidOperationException($"Secrets:CurrentKekVersion {CurrentVersion} has no key in Secrets:Keks.");
        }
    }

    public short CurrentVersion { get; }

    public byte[] Seal(ReadOnlySpan<byte> secret, EnvelopeBinding binding) =>
        Seal(secret, $"{binding.KindName}|{binding.CardSerial}", Aad(binding));

    /// <summary>Returns the secret; the caller zeroes it when done.</summary>
    /// <exception cref="CryptographicException">Wrong card, wrong issuance, wrong kind, or tampered.</exception>
    public byte[] Open(ReadOnlySpan<byte> envelope, EnvelopeBinding binding) =>
        Open(envelope, $"{binding.KindName}|{binding.CardSerial}", Aad(binding));

    /// <summary>
    /// An operator's TOTP secret, bound to their SID: an envelope copied onto
    /// another operator's row does not open (0027).
    /// </summary>
    public byte[] SealTotp(ReadOnlySpan<byte> secret, string operatorSid) =>
        Seal(secret, $"totp|{operatorSid}", Encoding.UTF8.GetBytes($"totp|{operatorSid}"));

    /// <exception cref="CryptographicException">Another operator's envelope, or tampered.</exception>
    public byte[] OpenTotp(ReadOnlySpan<byte> envelope, string operatorSid) =>
        Open(envelope, $"totp|{operatorSid}", Encoding.UTF8.GetBytes($"totp|{operatorSid}"));

    /// <summary>
    /// A backup code as stored: HMAC-SHA256 under a key derived from the KEK
    /// the operator's secret was sealed with.
    /// </summary>
    /// <remarks>
    /// Keyed, not a plain hash: a backup code has under fifty bits, and a
    /// stolen database dump would give them up to a GPU in a day. Without the
    /// KEK the stored values are just as useless as the envelopes next to them.
    /// </remarks>
    public byte[] BackupCodeHash(short kekVersion, string operatorSid, string normalisedCode)
    {
        if (!keks.TryGetValue(kekVersion, out var kek))
        {
            throw new CryptographicException($"KEK version {kekVersion} is not configured.");
        }

        var key = HKDF.Expand(HashAlgorithmName.SHA256, kek, 32,
            Encoding.UTF8.GetBytes($"blinkylite/backup-code/v1|{operatorSid}"));
        try
        {
            return HMACSHA256.HashData(key, Encoding.UTF8.GetBytes(normalisedCode));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
        }
    }

    // The context goes into the per-envelope key, so for card secrets it is
    // exactly the string it always was: every envelope already in a database
    // must still open.
    private byte[] Seal(ReadOnlySpan<byte> secret, string context, byte[] aad)
    {
        var envelope = new byte[HeaderSize + secret.Length + TagSize];
        envelope[0] = Format;
        BinaryPrimitives.WriteInt16BigEndian(envelope.AsSpan(1, 2), CurrentVersion);
        var nonce = envelope.AsSpan(3, NonceSize);
        RandomNumberGenerator.Fill(nonce);

        var key = EnvelopeKey(CurrentVersion, context, nonce);
        try
        {
            using var aes = new AesGcm(key, TagSize);
            aes.Encrypt(nonce, secret, envelope.AsSpan(HeaderSize, secret.Length),
                envelope.AsSpan(HeaderSize + secret.Length, TagSize), aad);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
        }

        return envelope;
    }

    private byte[] Open(ReadOnlySpan<byte> envelope, string context, byte[] aad)
    {
        if (envelope.Length < HeaderSize + TagSize || envelope[0] != Format)
        {
            throw new CryptographicException("Not a BlinkyLite envelope, or a format this build does not know.");
        }

        var version = BinaryPrimitives.ReadInt16BigEndian(envelope.Slice(1, 2));
        var nonce = envelope.Slice(3, NonceSize);
        var ciphertext = envelope[HeaderSize..^TagSize];
        var secret = new byte[ciphertext.Length];

        var key = EnvelopeKey(version, context, nonce);
        try
        {
            using var aes = new AesGcm(key, TagSize);
            aes.Decrypt(nonce, ciphertext, envelope[^TagSize..], secret, aad);
            return secret;
        }
        catch
        {
            CryptographicOperations.ZeroMemory(secret);
            throw;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
        }
    }

    private byte[] EnvelopeKey(short version, string context, ReadOnlySpan<byte> nonce)
    {
        if (!keks.TryGetValue(version, out var kek))
        {
            throw new CryptographicException($"The envelope was sealed with KEK version {version}, which is not configured.");
        }

        var info = Encoding.UTF8.GetBytes($"blinkylite/secret/v1|{context}|{Convert.ToHexStringLower(nonce)}");
        return HKDF.Expand(HashAlgorithmName.SHA256, kek, 32, info);
    }

    private static byte[] Aad(EnvelopeBinding binding) =>
        Encoding.UTF8.GetBytes($"{binding.KindName}|{binding.CardSerial}|{binding.IssuanceId:D}");
}

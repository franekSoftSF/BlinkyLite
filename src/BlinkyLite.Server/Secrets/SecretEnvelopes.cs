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
/// AES-256-GCM envelopes for PUKs and management keys (docs/03-data-model.md):
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

    public byte[] Seal(ReadOnlySpan<byte> secret, EnvelopeBinding binding)
    {
        var envelope = new byte[HeaderSize + secret.Length + TagSize];
        envelope[0] = Format;
        BinaryPrimitives.WriteInt16BigEndian(envelope.AsSpan(1, 2), CurrentVersion);
        var nonce = envelope.AsSpan(3, NonceSize);
        RandomNumberGenerator.Fill(nonce);

        var key = EnvelopeKey(CurrentVersion, binding, nonce);
        try
        {
            using var aes = new AesGcm(key, TagSize);
            aes.Encrypt(nonce, secret, envelope.AsSpan(HeaderSize, secret.Length),
                envelope.AsSpan(HeaderSize + secret.Length, TagSize), Aad(binding));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
        }

        return envelope;
    }

    /// <summary>Returns the secret; the caller zeroes it when done.</summary>
    /// <exception cref="CryptographicException">Wrong card, wrong issuance, wrong kind, or tampered.</exception>
    public byte[] Open(ReadOnlySpan<byte> envelope, EnvelopeBinding binding)
    {
        if (envelope.Length < HeaderSize + TagSize || envelope[0] != Format)
        {
            throw new CryptographicException("Not a BlinkyLite envelope, or a format this build does not know.");
        }

        var version = BinaryPrimitives.ReadInt16BigEndian(envelope.Slice(1, 2));
        var nonce = envelope.Slice(3, NonceSize);
        var ciphertext = envelope[HeaderSize..^TagSize];
        var secret = new byte[ciphertext.Length];

        var key = EnvelopeKey(version, binding, nonce);
        try
        {
            using var aes = new AesGcm(key, TagSize);
            aes.Decrypt(nonce, ciphertext, envelope[^TagSize..], secret, Aad(binding));
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

    private byte[] EnvelopeKey(short version, EnvelopeBinding binding, ReadOnlySpan<byte> nonce)
    {
        if (!keks.TryGetValue(version, out var kek))
        {
            throw new CryptographicException($"The envelope was sealed with KEK version {version}, which is not configured.");
        }

        var info = Encoding.UTF8.GetBytes(
            $"blinkylite/secret/v1|{binding.KindName}|{binding.CardSerial}|{Convert.ToHexStringLower(nonce)}");
        return HKDF.Expand(HashAlgorithmName.SHA256, kek, 32, info);
    }

    private static byte[] Aad(EnvelopeBinding binding) =>
        Encoding.UTF8.GetBytes($"{binding.KindName}|{binding.CardSerial}|{binding.IssuanceId:D}");
}

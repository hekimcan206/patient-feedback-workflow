using System.Security.Cryptography;
using System.Text;

namespace PatientFeedback.Privacy;

/// <summary>A key-encryption key (KEK). In production this lives in an HSM/KMS; here it comes from configuration.</summary>
public sealed class KeyEncryptionKey
{
    public KeyEncryptionKey(string id, byte[] key)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        ArgumentNullException.ThrowIfNull(key);
        if (id.Length > 32)
        {
            throw new ArgumentException("Key id is limited to 32 characters.", nameof(id));
        }

        if (key.Length != 32)
        {
            throw new ArgumentException("A KEK must be 256 bits.", nameof(key));
        }

        Id = id;
        Key = key;
    }

    public string Id { get; }

    internal byte[] Key { get; }

    public static KeyEncryptionKey FromBase64(string id, string base64) => new(id, Convert.FromBase64String(base64));
}

/// <summary>The data key of one record, wrapped by a KEK. Stored next to the ciphertext with the KEK id.</summary>
public sealed record WrappedKey(string KekId, byte[] Blob);

/// <summary>An unwrapped per-record data key. Never persisted, never logged.</summary>
public sealed class DataKey : IDisposable
{
    internal DataKey(byte[] key) => Key = key;

    internal byte[] Key { get; }

    public void Dispose() => CryptographicOperations.ZeroMemory(Key);
}

/// <summary>
/// Envelope encryption for personal data at rest, AES-256-GCM throughout.
/// <list type="bullet">
/// <item>Each contact record gets its own random data key (DEK). Fields are encrypted with the DEK; the DEK is
/// wrapped by the active KEK and stored with the KEK id, so KEK rotation re-wraps 48-byte keys instead of
/// re-encrypting every field.</item>
/// <item>Associated data binds every ciphertext to its record and field (<c>contact:{id}:{field}</c>); a ciphertext
/// copied into another row or column fails authentication instead of decrypting as someone else's data.</item>
/// <item>Destroying the wrapped DEK (retention job) makes every remaining copy of that record's ciphertext — backups,
/// replicas — permanently unreadable: crypto-shredding.</item>
/// </list>
/// </summary>
public sealed class PiiProtector
{
    private const byte FormatVersion = 1;
    private const int NonceSize = 12;
    private const int TagSize = 16;
    private readonly Dictionary<string, KeyEncryptionKey> _keks;
    private readonly KeyEncryptionKey _active;

    public PiiProtector(IEnumerable<KeyEncryptionKey> keks, string activeKekId)
    {
        ArgumentNullException.ThrowIfNull(keks);
        _keks = keks.ToDictionary(k => k.Id, StringComparer.Ordinal);
        _active = _keks.TryGetValue(activeKekId, out var active)
            ? active
            : throw new ArgumentException($"Active KEK '{activeKekId}' is not in the keyring.", nameof(activeKekId));
    }

    public string ActiveKekId => _active.Id;

    /// <summary>Creates a new DEK for <paramref name="recordId"/> and returns it with its wrapped form.</summary>
    public (DataKey Key, WrappedKey Wrapped) CreateDataKey(string recordId)
    {
        var dek = RandomNumberGenerator.GetBytes(32);
        return (new DataKey(dek), Wrap(dek, recordId, _active));
    }

    public DataKey Unwrap(WrappedKey wrapped, string recordId)
    {
        ArgumentNullException.ThrowIfNull(wrapped);
        if (!_keks.TryGetValue(wrapped.KekId, out var kek))
        {
            throw new CryptographicException($"KEK '{wrapped.KekId}' is not available.");
        }

        return new DataKey(Open(kek.Key, wrapped.Blob, KeyAad(recordId)));
    }

    /// <summary>Re-wraps a DEK under the active KEK (rotation). Field ciphertexts are unchanged.</summary>
    public WrappedKey Rewrap(WrappedKey wrapped, string recordId)
    {
        using var dek = Unwrap(wrapped, recordId);
        return Wrap(dek.Key, recordId, _active);
    }

    public static byte[] Encrypt(DataKey key, string recordId, string field, string plaintext)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(plaintext);
        return Seal(key.Key, Encoding.UTF8.GetBytes(plaintext), FieldAad(recordId, field));
    }

    public static string Decrypt(DataKey key, string recordId, string field, byte[] ciphertext)
    {
        ArgumentNullException.ThrowIfNull(key);
        return Encoding.UTF8.GetString(Open(key.Key, ciphertext, FieldAad(recordId, field)));
    }

    private static WrappedKey Wrap(byte[] dek, string recordId, KeyEncryptionKey kek) =>
        new(kek.Id, Seal(kek.Key, dek, KeyAad(recordId)));

    private static byte[] KeyAad(string recordId) => Encoding.UTF8.GetBytes("dek:" + recordId);

    private static byte[] FieldAad(string recordId, string field) => Encoding.UTF8.GetBytes("contact:" + recordId + ":" + field);

    // Layout: version(1) | nonce(12) | tag(16) | ciphertext(n). Random 96-bit nonces are safe here because every DEK
    // encrypts only a handful of fields, far below the 2^32 messages-per-key bound for random GCM nonces.
    private static byte[] Seal(byte[] key, byte[] plaintext, byte[] aad)
    {
        var output = new byte[1 + NonceSize + TagSize + plaintext.Length];
        output[0] = FormatVersion;
        var nonce = output.AsSpan(1, NonceSize);
        RandomNumberGenerator.Fill(nonce);
        using var gcm = new AesGcm(key, TagSize);
        gcm.Encrypt(nonce, plaintext, output.AsSpan(1 + NonceSize + TagSize), output.AsSpan(1 + NonceSize, TagSize), aad);
        return output;
    }

    private static byte[] Open(byte[] key, byte[] blob, byte[] aad)
    {
        ArgumentNullException.ThrowIfNull(blob);
        if (blob.Length < 1 + NonceSize + TagSize || blob[0] != FormatVersion)
        {
            throw new CryptographicException("Unsupported ciphertext format.");
        }

        var plaintext = new byte[blob.Length - 1 - NonceSize - TagSize];
        using var gcm = new AesGcm(key, TagSize);
        gcm.Decrypt(blob.AsSpan(1, NonceSize), blob.AsSpan(1 + NonceSize + TagSize), blob.AsSpan(1 + NonceSize, TagSize), plaintext, aad);
        return plaintext;
    }
}

/// <summary>
/// Keyed blind index (HMAC-SHA256) of the hospital's patient reference: lets the same patient be recognised across
/// visits (opt-out, de-duplication) without storing the reference itself. A separate key from the KEKs.
/// </summary>
public sealed class BlindIndex(byte[] key)
{
    public byte[] Compute(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return HMACSHA256.HashData(key, Encoding.UTF8.GetBytes(value.Trim().ToUpperInvariant()));
    }
}

/// <summary>Display masking for case handlers who have not asked to reveal the contact.</summary>
public static class PiiMask
{
    public static string Phone(string phone)
    {
        ArgumentNullException.ThrowIfNull(phone);
        var digits = new string([.. phone.Where(char.IsDigit)]);
        return digits.Length <= 4 ? "****" : new string('*', digits.Length - 4) + digits[^4..];
    }

    public static string Name(string fullName)
    {
        ArgumentNullException.ThrowIfNull(fullName);
        return string.Join(' ', fullName.Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(p => p[0] + new string('*', Math.Max(2, p.Length - 1))));
    }
}

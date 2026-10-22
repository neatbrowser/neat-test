using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace neat;

/// <summary>
/// The one cipher the vault uses: AES-256-GCM from .NET itself, nothing home-made and no
/// extra package. A sealed value is stored as nonce + ciphertext + tag in one block.
///
/// Every value gets its own random 12-byte nonce. With a random nonce a key stays safe for
/// far more values than any vault will ever hold, and the vault also changes its data key
/// whenever the way it is protected changes (see Vault.Switch).
///
/// The "associated data" is what ties a value to its place: it is not encrypted but it is
/// checked, so a sealed value cannot be moved to another entry, another site or another
/// field (user name into the password slot) without the check failing.
/// </summary>
internal static class Seal
{
    public const int KeyBytes = 32;
    public const int NonceBytes = 12;
    public const int TagBytes = 16;

    public static byte[] NewKey()
    {
        return RandomNumberGenerator.GetBytes(KeyBytes);
    }

    public static byte[] Encrypt(byte[] key, ReadOnlySpan<byte> plain, byte[] aad)
    {
        var blob = new byte[NonceBytes + plain.Length + TagBytes];
        var nonce = blob.AsSpan(0, NonceBytes);
        var cipher = blob.AsSpan(NonceBytes, plain.Length);
        var tag = blob.AsSpan(NonceBytes + plain.Length, TagBytes);

        RandomNumberGenerator.Fill(nonce);

        using var gcm = new AesGcm(key, TagBytes);
        gcm.Encrypt(nonce, plain, cipher, tag, aad);

        return blob;
    }

    /// <summary>
    /// The plain bytes, or null when the block does not check out: wrong key, changed
    /// content, or moved to a place it was not sealed for. The caller wipes what it gets.
    /// </summary>
    public static byte[]? Decrypt(byte[] key, byte[] blob, byte[] aad)
    {
        if (blob.Length < NonceBytes + TagBytes)
            return null;

        var length = blob.Length - NonceBytes - TagBytes;
        var plain = new byte[length];

        try
        {
            using var gcm = new AesGcm(key, TagBytes);
            gcm.Decrypt(
                blob.AsSpan(0, NonceBytes),
                blob.AsSpan(NonceBytes, length),
                blob.AsSpan(NonceBytes + length, TagBytes),
                plain,
                aad);

            return plain;
        }
        catch (CryptographicException)
        {
            CryptographicOperations.ZeroMemory(plain);
            return null;
        }
    }

    public static void Wipe(byte[]? bytes)
    {
        if (bytes is not null)
            CryptographicOperations.ZeroMemory(bytes);
    }
}

/// <summary>
/// Builds the associated data. Every part is written with its length in front, so two
/// different sets of parts can never produce the same bytes ("ab"+"c" against "a"+"bc").
/// </summary>
internal static class Aad
{
    private static byte[] Build(string purpose, params string[] parts)
    {
        using var stream = new MemoryStream();

        using (var w = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true))
        {
            w.Write("neat-vault");
            w.Write((byte)Vault.FormatVersion);
            w.Write(purpose);

            foreach (var part in parts)
                w.Write(part);
        }

        return stream.ToArray();
    }

    /// <summary>For one field of one entry: the entry, the site it belongs to, and which field.</summary>
    public static byte[] Field(Guid id, string site, string field)
    {
        return Build("field", id.ToString("N"), site, field);
    }

    /// <summary>For the data key sealed with a password-derived key: its salt and iteration count.</summary>
    public static byte[] Wrap(byte[] salt, int iterations)
    {
        return Build("wrap", Convert.ToBase64String(salt), iterations.ToString(CultureInfo.InvariantCulture));
    }
}

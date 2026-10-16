using System.Security.Cryptography;
using System.Text;
using Silo.Contracts.Providers.Crypto;

namespace Silo.Crypto;

/// <summary>
///     The bytes of a sealed secret: AES-256-GCM with a fresh random nonce for every seal. Pure, so it can be tested on its own.
/// </summary>
/// <remarks>
///     Layout: <c>'S' 'E'</c>, version (1 byte), the id of the data key (4 bytes, little endian), nonce (12), tag (16), cipher text. The
///     context (what kind of secret it is) is not stored: it is authenticated, so a secret cannot be moved into a place that expects
///     another kind. The key id says which data key to open it with, so keys can be replaced without losing what was sealed with the old one.
/// </remarks>
public static class SecretEnvelope
{
    public const int KeySize   = 32;
    public const int NonceSize = 12;
    public const int TagSize   = 16;

    private const byte Version = 1;
    private const int  Header  = 2 + 1 + 4;

    /// <summary>A new random data key.</summary>
    public static byte[] NewKey() => RandomNumberGenerator.GetBytes(KeySize);

    /// <summary>Seals <paramref name="plaintext" /> with the key, saying in the result which key it was (<paramref name="keyId" />).</summary>
    public static byte[] Seal(byte[] key, int keyId, byte[] plaintext, string context)
    {
        CheckKey(key);
        ArgumentNullException.ThrowIfNull(plaintext);

        var result = new byte[Header + NonceSize + TagSize + plaintext.Length];
        result[0] = (byte)'S';
        result[1] = (byte)'E';
        result[2] = Version;
        BitConverter.TryWriteBytes(result.AsSpan(3, 4), keyId);

        var nonce = result.AsSpan(Header, NonceSize);
        var tag   = result.AsSpan(Header + NonceSize, TagSize);
        var data  = result.AsSpan(Header + NonceSize + TagSize);
        RandomNumberGenerator.Fill(nonce);

        using var aes = new AesGcm(key, TagSize);
        aes.Encrypt(nonce, plaintext, data, tag, Encoding.UTF8.GetBytes(context ?? string.Empty));
        return result;
    }

    /// <summary>The id of the key that sealed the bytes.</summary>
    /// <exception cref="SecretException">They are not a sealed secret.</exception>
    public static int KeyIdOf(byte[] sealedData)
    {
        if (sealedData is null || sealedData.Length < Header + NonceSize + TagSize || sealedData[0] != 'S' || sealedData[1] != 'E')
            throw new SecretException("The stored secret is not in a format this version of Silo understands.");

        if (sealedData[2] != Version)
            throw new SecretException($"The stored secret was written by a newer version of Silo (format {sealedData[2]}).");

        return BitConverter.ToInt32(sealedData, 3);
    }

    /// <summary>Opens a sealed secret.</summary>
    /// <exception cref="SecretException">The bytes are not a sealed secret, were changed, or were sealed with another key or for another context.</exception>
    public static byte[] Open(byte[] key, byte[] sealedData, string context)
    {
        CheckKey(key);
        KeyIdOf(sealedData); // checks the shape

        var nonce  = sealedData.AsSpan(Header, NonceSize);
        var tag    = sealedData.AsSpan(Header + NonceSize, TagSize);
        var data   = sealedData.AsSpan(Header + NonceSize + TagSize);
        var result = new byte[data.Length];

        try
        {
            using var aes = new AesGcm(key, TagSize);
            aes.Decrypt(nonce, data, tag, result, Encoding.UTF8.GetBytes(context ?? string.Empty));
            return result;
        }
        catch (CryptographicException ex)
        {
            throw new SecretException("The stored secret cannot be opened: it was changed, or it belongs to another key.", ex);
        }
    }

    private static void CheckKey(byte[] key)
    {
        if (key is null || key.Length != KeySize) throw new ArgumentException($"A data key is {KeySize} bytes.", nameof(key));
    }
}

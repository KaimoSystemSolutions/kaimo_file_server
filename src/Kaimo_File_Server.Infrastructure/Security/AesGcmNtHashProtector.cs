using System.Security.Cryptography;
using System.Text;
using Kaimo_File_Server.Core.Security;
using Microsoft.Extensions.Configuration;

namespace Kaimo_File_Server.Infrastructure.Security;

/// <summary>
/// AES-256-GCM implementation of <see cref="INtHashProtector"/>. The key is derived (SHA-256) from a
/// server-held secret supplied via configuration key <c>NtHash:EncryptionKey</c> (env
/// <c>NtHash__EncryptionKey</c>), mirroring the mandatory <c>Jwt:Secret</c>. A missing/blank secret
/// fails closed at construction — the server will not start without it.
///
/// Stored format: <c>"enc:" + Base64(nonce(12) || tag(16) || ciphertext)</c>. A value without the
/// <c>enc:</c> prefix is treated as a legacy plaintext hex hash and returned unchanged by
/// <see cref="Unprotect"/> (so old rows keep working and are re-encrypted on next write).
/// </summary>
public sealed class AesGcmNtHashProtector : INtHashProtector
{
    private const string Prefix = "enc:";
    private const int NonceLen = 12; // AesGcm.NonceByteSizes.MaxSize
    private const int TagLen = 16;   // AesGcm.TagByteSizes.MaxSize

    private readonly byte[] _key;

    public AesGcmNtHashProtector(IConfiguration configuration)
    {
        var secret = configuration["NtHash:EncryptionKey"];
        if (string.IsNullOrWhiteSpace(secret))
            throw new InvalidOperationException(
                "NtHash:EncryptionKey (env NtHash__EncryptionKey) is not configured. It is required " +
                "to encrypt SMB NT hashes at rest — the server refuses to start without it.");

        byte[] secretBytes = Encoding.UTF8.GetBytes(secret);
        try
        {
            _key = SHA256.HashData(secretBytes);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(secretBytes);
        }
    }

    /// <summary>Test/explicit-key constructor. The key must be exactly 32 bytes.</summary>
    internal AesGcmNtHashProtector(byte[] key)
    {
        if (key is not { Length: 32 })
            throw new ArgumentException("Key must be 32 bytes (AES-256).", nameof(key));
        _key = key;
    }

    public string Protect(string plaintextNtHashHex)
    {
        if (string.IsNullOrEmpty(plaintextNtHashHex))
            return plaintextNtHashHex;

        // Already encrypted → never double-wrap.
        if (plaintextNtHashHex.StartsWith(Prefix, StringComparison.Ordinal))
            return plaintextNtHashHex;

        byte[] plain = Encoding.UTF8.GetBytes(plaintextNtHashHex);
        byte[] nonce = RandomNumberGenerator.GetBytes(NonceLen);
        byte[] cipher = new byte[plain.Length];
        byte[] tag = new byte[TagLen];

        try
        {
            using var aes = new AesGcm(_key, TagLen);
            aes.Encrypt(nonce, plain, cipher, tag);

            byte[] blob = new byte[NonceLen + TagLen + cipher.Length];
            try
            {
                Buffer.BlockCopy(nonce, 0, blob, 0, NonceLen);
                Buffer.BlockCopy(tag, 0, blob, NonceLen, TagLen);
                Buffer.BlockCopy(cipher, 0, blob, NonceLen + TagLen, cipher.Length);
                return Prefix + Convert.ToBase64String(blob);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(blob);
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plain);
        }
    }

    public string Unprotect(string storedNtHash)
    {
        if (string.IsNullOrEmpty(storedNtHash))
            return storedNtHash;

        // Legacy plaintext hex (pre-encryption row) → return unchanged.
        if (!storedNtHash.StartsWith(Prefix, StringComparison.Ordinal))
            return storedNtHash;

        byte[] blob = Convert.FromBase64String(storedNtHash[Prefix.Length..]);
        if (blob.Length < NonceLen + TagLen)
            throw new CryptographicException("Stored NT hash is too short to be valid ciphertext.");

        var nonce = blob.AsSpan(0, NonceLen);
        var tag = blob.AsSpan(NonceLen, TagLen);
        var cipher = blob.AsSpan(NonceLen + TagLen);
        byte[] plain = new byte[cipher.Length];

        try
        {
            using var aes = new AesGcm(_key, TagLen);
            aes.Decrypt(nonce, cipher, tag, plain); // throws CryptographicException on tamper / wrong key
            return Encoding.UTF8.GetString(plain);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plain);
            CryptographicOperations.ZeroMemory(blob);
        }
    }

    public byte[] UnprotectToBytes(string storedNtHash)
    {
        ArgumentNullException.ThrowIfNull(storedNtHash);

        if (!storedNtHash.StartsWith(Prefix, StringComparison.Ordinal))
            return DecodeHex(storedNtHash.AsSpan());

        byte[] blob = Convert.FromBase64String(storedNtHash[Prefix.Length..]);
        byte[]? plaintext = null;
        try
        {
            if (blob.Length < NonceLen + TagLen)
                throw new CryptographicException(
                    "Stored NT hash is too short to be valid ciphertext.");

            ReadOnlySpan<byte> nonce = blob.AsSpan(0, NonceLen);
            ReadOnlySpan<byte> tag = blob.AsSpan(NonceLen, TagLen);
            ReadOnlySpan<byte> cipher = blob.AsSpan(NonceLen + TagLen);
            plaintext = new byte[cipher.Length];

            using var aes = new AesGcm(_key, TagLen);
            aes.Decrypt(nonce, cipher, tag, plaintext);
            return DecodeAsciiHex(plaintext);
        }
        finally
        {
            if (plaintext is not null)
                CryptographicOperations.ZeroMemory(plaintext);
            CryptographicOperations.ZeroMemory(blob);
        }
    }

    private static byte[] DecodeHex(ReadOnlySpan<char> hex)
    {
        if (hex.Length != 32)
            throw new FormatException("An NT hash must contain exactly 32 hexadecimal characters.");

        byte[] result = new byte[16];
        for (int index = 0; index < result.Length; index++)
        {
            int high = HexValue(hex[index * 2]);
            int low = HexValue(hex[index * 2 + 1]);
            if (high < 0 || low < 0)
            {
                CryptographicOperations.ZeroMemory(result);
                throw new FormatException("The stored NT hash is not valid hexadecimal.");
            }
            result[index] = (byte)((high << 4) | low);
        }
        return result;
    }

    private static byte[] DecodeAsciiHex(ReadOnlySpan<byte> hex)
    {
        if (hex.Length != 32)
            throw new FormatException("An NT hash must contain exactly 32 hexadecimal characters.");

        byte[] result = new byte[16];
        for (int index = 0; index < result.Length; index++)
        {
            int high = HexValue(hex[index * 2]);
            int low = HexValue(hex[index * 2 + 1]);
            if (high < 0 || low < 0)
            {
                CryptographicOperations.ZeroMemory(result);
                throw new FormatException("The stored NT hash is not valid hexadecimal.");
            }
            result[index] = (byte)((high << 4) | low);
        }
        return result;
    }

    private static int HexValue(byte value) => value switch
    {
        >= (byte)'0' and <= (byte)'9' => value - (byte)'0',
        >= (byte)'A' and <= (byte)'F' => value - (byte)'A' + 10,
        >= (byte)'a' and <= (byte)'f' => value - (byte)'a' + 10,
        _ => -1,
    };

    private static int HexValue(char value) => value switch
    {
        >= '0' and <= '9' => value - '0',
        >= 'A' and <= 'F' => value - 'A' + 10,
        >= 'a' and <= 'f' => value - 'a' + 10,
        _ => -1,
    };
}

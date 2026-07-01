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

        _key = SHA256.HashData(Encoding.UTF8.GetBytes(secret));
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

        using var aes = new AesGcm(_key, TagLen);
        aes.Encrypt(nonce, plain, cipher, tag);

        byte[] blob = new byte[NonceLen + TagLen + cipher.Length];
        Buffer.BlockCopy(nonce, 0, blob, 0, NonceLen);
        Buffer.BlockCopy(tag, 0, blob, NonceLen, TagLen);
        Buffer.BlockCopy(cipher, 0, blob, NonceLen + TagLen, cipher.Length);

        return Prefix + Convert.ToBase64String(blob);
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

        using var aes = new AesGcm(_key, TagLen);
        aes.Decrypt(nonce, cipher, tag, plain); // throws CryptographicException on tamper / wrong key

        return Encoding.UTF8.GetString(plain);
    }
}

using System.Security.Cryptography;
using Kaimo_File_Server.Core.Security;
using Kaimo_File_Server.Infrastructure.Configuration;
using Kaimo_File_Server.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Kaimo_File_Server.Infrastructure.Security;

/// <summary>
/// Detects an <c>NtHash:EncryptionKey</c> that differs from the key the stored
/// NT hashes were encrypted with. Without this check a mismatched key between
/// Host, Web and SmbBridge (or a lost key after a restore) makes every stored
/// hash undecryptable, and each SMB login just fails as "wrong password".
///
/// The Host owns the canary: it creates it once (after confirming the key can
/// read existing hashes) and verifies it on every start. Web and SmbBridge only
/// verify. Every process refuses to start on a mismatch.
/// </summary>
public static class NtHashKeyCanary
{
    public const string ConfigKey = "security.ntHash.keyCanary";
    private const string CanaryPlaintext = "kaimo-nt-hash-key-canary";
    private const string EncryptedPrefix = "enc:";

    /// <summary>
    /// Host: verifies or creates the canary and encrypts legacy plaintext NT
    /// hashes, so a database or backup reader no longer finds password
    /// equivalents in clear text.
    /// </summary>
    public static async Task EnsureAsync(
        ApplicationDbContext db, INtHashProtector protector, ILogger logger, CancellationToken cancellationToken = default)
    {
        var canary = await db.ConfigSettings.SingleOrDefaultAsync(s => s.Key == ConfigKey, cancellationToken);
        if (canary is not null)
        {
            Verify(canary.Value, protector);
        }
        else
        {
            await EnsureKeyReadsExistingHashesAsync(db, protector, logger, cancellationToken);
            db.ConfigSettings.Add(new ConfigSetting
            {
                Key = ConfigKey,
                Value = protector.Protect(CanaryPlaintext),
                UpdatedAt = DateTime.UtcNow
            });
            await db.SaveChangesAsync(cancellationToken);
        }

        await EncryptLegacyPlaintextAsync(db, protector, logger, cancellationToken);
    }

    /// <summary>Web / SmbBridge: fail fast when this process holds a different key.</summary>
    public static async Task VerifyAsync(
        ApplicationDbContext db, INtHashProtector protector, ILogger logger, CancellationToken cancellationToken = default)
    {
        var value = await db.ConfigSettings.Where(s => s.Key == ConfigKey)
            .Select(s => s.Value).SingleOrDefaultAsync(cancellationToken);
        if (value is null)
        {
            // The Host seeds after migrations; readiness may be reported first.
            logger.LogWarning("NT-hash key canary not present yet; key consistency is verified by the Host.");
            return;
        }
        Verify(value, protector);
    }

    private static void Verify(string storedCanary, INtHashProtector protector)
    {
        try
        {
            if (protector.Unprotect(storedCanary) == CanaryPlaintext)
                return;
        }
        catch (Exception exception) when (exception is CryptographicException or FormatException)
        {
            // Reported below.
        }
        throw new InvalidOperationException(KeyMismatchMessage);
    }

    private static async Task EnsureKeyReadsExistingHashesAsync(
        ApplicationDbContext db, INtHashProtector protector, ILogger logger, CancellationToken cancellationToken)
    {
        var encrypted = await db.Users.Where(u => u.NtHash.StartsWith(EncryptedPrefix))
            .Select(u => u.NtHash).ToListAsync(cancellationToken);
        if (encrypted.Count == 0)
            return;

        int unreadable = encrypted.Count(stored => !CanRead(protector, stored));
        // Some unreadable rows can predate an earlier key change; only a key that
        // reads none of them is certainly wrong, and must not be recorded as good.
        if (unreadable == encrypted.Count)
            throw new InvalidOperationException(KeyMismatchMessage);
        if (unreadable > 0)
            logger.LogWarning(
                "{Count} stored NT hashes cannot be decrypted with the configured NtHash:EncryptionKey; " +
                "those users must set a new password before they can use SMB.", unreadable);
    }

    private static async Task EncryptLegacyPlaintextAsync(
        ApplicationDbContext db, INtHashProtector protector, ILogger logger, CancellationToken cancellationToken)
    {
        var legacy = await db.Users
            .Where(u => u.NtHash != "" && !u.NtHash.StartsWith(EncryptedPrefix))
            .Select(u => new { u.Id, u.NtHash })
            .ToListAsync(cancellationToken);
        foreach (var row in legacy)
        {
            string protectedHash = protector.Protect(row.NtHash);
            // Conditional so a concurrent password change is never overwritten.
            await db.Users.Where(u => u.Id == row.Id && u.NtHash == row.NtHash)
                .ExecuteUpdateAsync(set => set.SetProperty(u => u.NtHash, protectedHash), cancellationToken);
        }
        if (legacy.Count > 0)
            logger.LogInformation("Encrypted {Count} legacy plaintext NT hashes at rest.", legacy.Count);
    }

    private static bool CanRead(INtHashProtector protector, string stored)
    {
        try
        {
            CryptographicOperations.ZeroMemory(protector.UnprotectToBytes(stored));
            return true;
        }
        catch (Exception exception) when (exception is CryptographicException or FormatException)
        {
            return false;
        }
    }

    private const string KeyMismatchMessage =
        "NtHash:EncryptionKey does not match the key the stored SMB NT hashes were encrypted with. " +
        "Host, Web and SmbBridge must use the identical key (env NtHash__EncryptionKey), and a restored " +
        "database needs the key it was created with. Refusing to start instead of silently rejecting " +
        "every SMB login.";
}

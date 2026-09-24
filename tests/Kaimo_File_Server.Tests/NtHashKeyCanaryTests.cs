using Kaimo_File_Server.Core.Domain.Identity;
using Kaimo_File_Server.Infrastructure.Security;
using Kaimo_File_Server.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Kaimo_File_Server.Tests;

/// <summary>
/// A different NtHash:EncryptionKey in one container used to surface only as
/// "every SMB login fails". These tests pin down that a mismatch stops startup
/// instead, and that legacy plaintext NT hashes get encrypted at rest.
/// </summary>
public sealed class NtHashKeyCanaryTests : DatabaseTestBase
{
    private const string SampleHash = "31D6CFE0D16AE931B73C59D7E0C089C1";

    private readonly AesGcmNtHashProtector _key = Protector("the-real-key");
    private readonly AesGcmNtHashProtector _otherKey = Protector("a-different-key");

    [Fact]
    public async Task Ensure_CreatesCanary_ThatSameKeyVerifies()
    {
        await EnsureAsync(_key);
        await EnsureAsync(_key);

        using var db = NewContext();
        await NtHashKeyCanary.VerifyAsync(db, _key, NullLogger.Instance);
        Assert.Single(await db.ConfigSettings.Where(s => s.Key == NtHashKeyCanary.ConfigKey).ToListAsync());
    }

    [Fact]
    public async Task VerifyAndEnsure_WithDifferentKey_Throw()
    {
        await EnsureAsync(_key);

        using var db = NewContext();
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => NtHashKeyCanary.VerifyAsync(db, _otherKey, NullLogger.Instance));
        await Assert.ThrowsAsync<InvalidOperationException>(() => EnsureAsync(_otherKey));
    }

    [Fact]
    public async Task Verify_WithoutCanaryYet_DoesNotBlockStartup()
    {
        using var db = NewContext();
        await NtHashKeyCanary.VerifyAsync(db, _otherKey, NullLogger.Instance);
    }

    [Fact]
    public async Task FirstEnsure_WithKeyThatReadsNoExistingHash_Throws_AndRecordsNothing()
    {
        // Upgrade scenario: hashes exist, canary does not, configured key is wrong.
        SeedUserWithNtHash("alice", _key.Protect(SampleHash));

        await Assert.ThrowsAsync<InvalidOperationException>(() => EnsureAsync(_otherKey));

        using var db = NewContext();
        Assert.False(await db.ConfigSettings.AnyAsync(s => s.Key == NtHashKeyCanary.ConfigKey));
    }

    [Fact]
    public async Task FirstEnsure_WithCorrectKey_AcceptsExistingHashes()
    {
        SeedUserWithNtHash("alice", _key.Protect(SampleHash));
        // One stale row from an earlier key must not block a key that reads the rest.
        SeedUserWithNtHash("bob", _otherKey.Protect(SampleHash));

        await EnsureAsync(_key);

        using var db = NewContext();
        await NtHashKeyCanary.VerifyAsync(db, _key, NullLogger.Instance);
    }

    [Fact]
    public async Task Ensure_EncryptsLegacyPlaintextHashes()
    {
        var legacy = SeedUserWithNtHash("carol", SampleHash);
        var empty = SeedUserWithNtHash("dave", "");

        await EnsureAsync(_key);

        using var db = NewContext();
        var stored = (await db.Users.SingleAsync(u => u.Id == legacy.Id)).NtHash;
        Assert.StartsWith("enc:", stored);
        Assert.Equal(SampleHash, _key.Unprotect(stored));
        Assert.Equal("", (await db.Users.SingleAsync(u => u.Id == empty.Id)).NtHash);
    }

    private async Task EnsureAsync(AesGcmNtHashProtector protector)
    {
        using var db = NewContext();
        await NtHashKeyCanary.EnsureAsync(db, protector, NullLogger.Instance);
    }

    private User SeedUserWithNtHash(string username, string ntHash)
    {
        var user = SeedUser(username);
        using var db = NewContext();
        db.Users.Where(u => u.Id == user.Id)
            .ExecuteUpdate(set => set.SetProperty(u => u.NtHash, ntHash));
        return user;
    }

    private static AesGcmNtHashProtector Protector(string key)
        => new(new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["NtHash:EncryptionKey"] = key })
            .Build());
}

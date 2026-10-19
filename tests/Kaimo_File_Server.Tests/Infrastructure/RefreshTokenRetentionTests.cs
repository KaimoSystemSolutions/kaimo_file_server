using Kaimo_File_Server.Core.Domain.ClientSync;
using Kaimo_File_Server.Infrastructure.Repositories;
using Kaimo_File_Server.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Kaimo_File_Server.Tests;

/// <summary>
/// Verifies <see cref="RefreshTokenRepository.PruneExpiredBeforeAsync"/> — the retention prune that
/// stops the append-only <c>refresh_tokens</c> table (one new row per rotation, old rows only marked
/// revoked) from growing without bound. The cutoff keys on expiry, never on revocation, so a
/// still-unexpired-but-revoked token stays for reuse detection.
/// </summary>
public sealed class RefreshTokenRetentionTests : DatabaseTestBase
{
    private static readonly DateTime Now = new(2026, 8, 27, 12, 0, 0, DateTimeKind.Utc);

    private RefreshTokenRepository Repo() => new(DbFactory);

    private Guid SeedDevice(Guid userId)
    {
        var device = new SyncDevice
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            DisplayName = "dev",
            CreatedAtUtc = Now.AddDays(-300),
            LastSeenUtc = Now,
        };
        using var db = NewContext();
        db.SyncDevices.Add(device);
        db.SaveChanges();
        return device.Id;
    }

    private void SeedToken(Guid userId, Guid deviceId, DateTime expiresAtUtc, DateTime? revokedAtUtc = null)
    {
        using var db = NewContext();
        db.RefreshTokens.Add(new RefreshToken
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            DeviceId = deviceId,
            TokenHash = Guid.NewGuid().ToString("N") + Guid.NewGuid().ToString("N"), // 64 hex chars
            CreatedAtUtc = expiresAtUtc.AddDays(-30),
            ExpiresAtUtc = expiresAtUtc,
            RevokedAtUtc = revokedAtUtc,
        });
        db.SaveChanges();
    }

    [Fact]
    public async Task PruneExpiredBefore_RemovesLongExpired_KeepsRecentAndLive()
    {
        var user = SeedUser("alice");
        var device = SeedDevice(user.Id);

        // Expired 100 days ago → pruned.
        SeedToken(user.Id, device, expiresAtUtc: Now.AddDays(-100));
        // Expired 100 days ago but revoked — still pruned; expiry alone decides.
        SeedToken(user.Id, device, expiresAtUtc: Now.AddDays(-100), revokedAtUtc: Now.AddDays(-100));
        // Expired only 2 days ago → within grace, kept (reuse detection still meaningful).
        SeedToken(user.Id, device, expiresAtUtc: Now.AddDays(-2));
        // Still valid → kept.
        SeedToken(user.Id, device, expiresAtUtc: Now.AddDays(20));

        var removed = await Repo().PruneExpiredBeforeAsync(Now.AddDays(-7));

        Assert.Equal(2, removed);

        await using var db = NewContext();
        Assert.Equal(2, await db.RefreshTokens.CountAsync(t => t.DeviceId == device));
    }

    /// <summary>
    /// Two concurrent refreshes read the same active token. Only the first rotation may
    /// succeed; the second must be refused and must not insert its replacement, otherwise
    /// a stolen refresh token could fork a second valid chain.
    /// </summary>
    [Fact]
    public async Task Rotate_SameTokenTwice_OnlyFirstWins()
    {
        var user = SeedUser("bob");
        var device = SeedDevice(user.Id);
        SeedToken(user.Id, device, expiresAtUtc: Now.AddDays(20));

        RefreshToken current;
        await using (var db = NewContext())
            current = await db.RefreshTokens.AsNoTracking().SingleAsync(t => t.DeviceId == device);

        RefreshToken NewReplacement() => new()
        {
            Id = Guid.NewGuid(),
            UserId = user.Id,
            DeviceId = device,
            TokenHash = Guid.NewGuid().ToString("N") + Guid.NewGuid().ToString("N"),
            CreatedAtUtc = Now,
            ExpiresAtUtc = Now.AddDays(30),
        };

        current.RevokedAtUtc = Now;
        var first = NewReplacement();
        var second = NewReplacement();

        Assert.True(await Repo().RotateAsync(current, first));
        Assert.False(await Repo().RotateAsync(current, second));

        await using var check = NewContext();
        Assert.Equal(2, await check.RefreshTokens.CountAsync(t => t.DeviceId == device));
        var rotated = await check.RefreshTokens.SingleAsync(t => t.Id == current.Id);
        Assert.Equal(first.Id, rotated.ReplacedByTokenId);
        Assert.False(await check.RefreshTokens.AnyAsync(t => t.Id == second.Id));
    }

    /// <summary>A password change must revoke every live refresh token of that user only.</summary>
    [Fact]
    public async Task UpdatePassword_RevokesRefreshTokensOfThatUser()
    {
        var carol = SeedUser("carol");
        var other = SeedUser("dave");
        var carolDevice = SeedDevice(carol.Id);
        var otherDevice = SeedDevice(other.Id);
        SeedToken(carol.Id, carolDevice, expiresAtUtc: Now.AddDays(20));
        SeedToken(carol.Id, carolDevice, expiresAtUtc: Now.AddDays(25));
        SeedToken(other.Id, otherDevice, expiresAtUtc: Now.AddDays(20));

        await new UserRepository(DbFactory).UpdatePasswordAsync(carol.Id, "bcrypt-hash", "enc:nt");

        await using var db = NewContext();
        Assert.False(await db.RefreshTokens.AnyAsync(t => t.UserId == carol.Id && t.RevokedAtUtc == null));
        Assert.True(await db.RefreshTokens.AnyAsync(t => t.UserId == other.Id && t.RevokedAtUtc == null));
    }
}

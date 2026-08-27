using Kaimo_File_Server.Core.Domain.ClientSync;
using Kaimo_File_Server.Infrastructure.Repositories;
using Kaimo_File_Server.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Kaimo_File_Server.Tests;

/// <summary>
/// Verifies <see cref="SyncDeviceRepository.DeleteRetiredAsync"/> — the retention prune
/// that keeps the client-device admin list free of registrations that can no longer reach
/// the server. Runs against the real Sqlite schema so the cascade to child rows is exercised
/// exactly as production would.
/// </summary>
public sealed class SyncDeviceRetentionTests : DatabaseTestBase
{
    private static readonly DateTime Now = new(2026, 8, 27, 12, 0, 0, DateTimeKind.Utc);

    private SyncDeviceRepository Repo() => new(DbFactory);

    private SyncDevice SeedDevice(
        Guid userId, DateTime lastSeenUtc, DateTime? revokedAtUtc = null)
    {
        var device = new SyncDevice
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            DisplayName = "dev",
            Platform = "android",
            CreatedAtUtc = lastSeenUtc,
            LastSeenUtc = lastSeenUtc,
            RevokedAtUtc = revokedAtUtc,
        };
        using var db = NewContext();
        db.SyncDevices.Add(device);
        db.SaveChanges();
        return device;
    }

    [Fact]
    public async Task DeleteRetired_RemovesRevokedAndInactive_KeepsLiveDevices()
    {
        var user = SeedUser("alice");

        // Live: seen yesterday, never revoked → kept.
        var live = SeedDevice(user.Id, lastSeenUtc: Now.AddDays(-1));
        // Recently revoked (2 days ago) → within grace, kept.
        var recentlyRevoked = SeedDevice(
            user.Id, lastSeenUtc: Now.AddDays(-2), revokedAtUtc: Now.AddDays(-2));
        // Revoked long ago (45 days) → pruned.
        var oldRevoked = SeedDevice(
            user.Id, lastSeenUtc: Now.AddDays(-45), revokedAtUtc: Now.AddDays(-45));
        // Never revoked but unseen for 200 days → pruned (its sign-in has long expired).
        var stale = SeedDevice(user.Id, lastSeenUtc: Now.AddDays(-200));

        var removed = await Repo().DeleteRetiredAsync(
            revokedBeforeUtc: Now.AddDays(-30),
            inactiveBeforeUtc: Now.AddDays(-90));

        Assert.Equal(2, removed);

        await using var db = NewContext();
        var surviving = await db.SyncDevices.Select(d => d.Id).ToListAsync();
        Assert.Contains(live.Id, surviving);
        Assert.Contains(recentlyRevoked.Id, surviving);
        Assert.DoesNotContain(oldRevoked.Id, surviving);
        Assert.DoesNotContain(stale.Id, surviving);
    }

    [Fact]
    public async Task DeleteRetired_CascadesToRefreshTokens()
    {
        var user = SeedUser("bob");
        var stale = SeedDevice(user.Id, lastSeenUtc: Now.AddDays(-200));

        using (var db = NewContext())
        {
            db.RefreshTokens.Add(new RefreshToken
            {
                Id = Guid.NewGuid(),
                UserId = user.Id,
                DeviceId = stale.Id,
                TokenHash = new string('a', 64),
                CreatedAtUtc = Now.AddDays(-200),
                ExpiresAtUtc = Now.AddDays(-170),
            });
            db.SaveChanges();
        }

        var removed = await Repo().DeleteRetiredAsync(
            revokedBeforeUtc: Now.AddDays(-30),
            inactiveBeforeUtc: Now.AddDays(-90));

        Assert.Equal(1, removed);

        await using var db2 = NewContext();
        Assert.False(await db2.SyncDevices.AnyAsync(d => d.Id == stale.Id));
        // The refresh token was removed by the ON DELETE CASCADE foreign key.
        Assert.False(await db2.RefreshTokens.AnyAsync(t => t.DeviceId == stale.Id));
    }
}

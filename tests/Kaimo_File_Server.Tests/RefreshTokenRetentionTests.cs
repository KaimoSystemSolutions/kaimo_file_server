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
}

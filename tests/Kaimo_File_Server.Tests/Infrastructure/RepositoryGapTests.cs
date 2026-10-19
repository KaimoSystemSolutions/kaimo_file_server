using Kaimo_File_Server.Core.Domain;
using Kaimo_File_Server.Core.Domain.ClientSync;
using Kaimo_File_Server.Core.Domain.Department;
using Kaimo_File_Server.Core.Domain.Identity;
using Kaimo_File_Server.Core.Security;
using Kaimo_File_Server.Core.Services.Sync;
using Kaimo_File_Server.Infrastructure.Repositories;
using Kaimo_File_Server.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Kaimo_File_Server.Tests;

/// <summary>
/// Real-database tests for repository behaviour that no other suite pins down:
/// link-table cleanup on principal delete (no FK cascade reaches those rows),
/// client-API idempotency receipts, the sync change cursor and per-device sync profiles,
/// and cloud-access grants.
/// </summary>
public sealed class RepositoryGapTests : DatabaseTestBase
{
    // ─────────────────────── Principal delete cleanup ───────────────────────

    private (Group Group, Department Dept, CloudAccessShare CloudShare) SeedLinkTargets()
    {
        var dept = SeedDepartment("Sales");
        var group = new Group(Guid.NewGuid(), "Staff");
        var connection = new StorageConnection
        {
            CreatedByUserId = Guid.NewGuid(),
            ProviderId = "onedrive",
            Name = "conn",
            AuthorizationMode = StorageAuthorizationMode.DeviceCode,
            State = StorageConnectionState.PendingAuthorization,
        };
        var cloudShare = new CloudAccessShare { ConnectionId = connection.Id, DepartmentId = dept.Id, Name = "cloud" };
        var role = new Role(Guid.NewGuid(), "R", ManagementPermission.None, false);

        using var db = NewContext();
        db.Groups.Add(group);
        db.StorageConnections.Add(connection);
        db.CloudAccessShares.Add(cloudShare);
        db.Roles.Add(role);
        db.SaveChanges();
        return (group, dept, cloudShare);
    }

    private void SeedLinks(Guid principalId, Group group, Department dept, CloudAccessShare cloudShare, bool isUser)
    {
        using var db = NewContext();
        if (isUser)
        {
            db.UserGroups.Add(new UserGroup(principalId, group.Id));
            db.DepartmentUsers.Add(new DepartmentUser(dept.Id, principalId));
        }
        db.ScopedRoleAssignments.Add(new ScopedRoleAssignment(principalId, db.Roles.First().Id, ScopeType.Global, Guid.Empty));
        db.CloudAccessGrants.Add(new CloudAccessGrant { ShareId = cloudShare.Id, PrincipalId = principalId });
        db.SaveChanges();
    }

    [Fact]
    public async Task UserDelete_RemovesEveryLinkRow_ButKeepsOtherUsers()
    {
        var (group, dept, cloudShare) = SeedLinkTargets();
        var alice = SeedUser("alice");
        var bob = SeedUser("bob");
        SeedLinks(alice.Id, group, dept, cloudShare, isUser: true);
        SeedLinks(bob.Id, group, dept, cloudShare, isUser: true);

        await UserRepo().DeleteAsync(alice.Id);

        await using var db = NewContext();
        Assert.False(await db.Users.AnyAsync(u => u.Id == alice.Id));
        Assert.False(await db.UserGroups.AnyAsync(x => x.UserId == alice.Id));
        Assert.False(await db.DepartmentUsers.AnyAsync(x => x.UserId == alice.Id));
        Assert.False(await db.ScopedRoleAssignments.AnyAsync(x => x.PrincipalId == alice.Id));
        Assert.False(await db.CloudAccessGrants.AnyAsync(x => x.PrincipalId == alice.Id));
        Assert.True(await db.UserGroups.AnyAsync(x => x.UserId == bob.Id));
        Assert.True(await db.CloudAccessGrants.AnyAsync(x => x.PrincipalId == bob.Id));
    }

    [Fact]
    public async Task GroupDelete_RemovesMembershipsAssignmentsAndGrants()
    {
        var (group, dept, cloudShare) = SeedLinkTargets();
        var alice = SeedUser("alice");
        SeedLinks(alice.Id, group, dept, cloudShare, isUser: true);
        SeedLinks(group.Id, group, dept, cloudShare, isUser: false);

        await GroupRepo().DeleteAsync(group.Id);

        await using var db = NewContext();
        Assert.False(await db.Groups.AnyAsync(g => g.Id == group.Id));
        Assert.False(await db.UserGroups.AnyAsync(x => x.GroupId == group.Id));
        Assert.False(await db.ScopedRoleAssignments.AnyAsync(x => x.PrincipalId == group.Id));
        Assert.False(await db.CloudAccessGrants.AnyAsync(x => x.PrincipalId == group.Id));
        Assert.True(await db.Users.AnyAsync(u => u.Id == alice.Id));
    }

    // ─────────────────────── Client request receipts ───────────────────────

    private SyncDevice SeedDevice()
    {
        var device = new SyncDevice
        {
            Id = Guid.NewGuid(),
            UserId = SeedUser("u" + Guid.NewGuid().ToString("N")[..12]).Id,
            DisplayName = "dev",
            Platform = "android",
            CreatedAtUtc = DateTime.UtcNow,
            LastSeenUtc = DateTime.UtcNow,
        };
        using var db = NewContext();
        db.SyncDevices.Add(device);
        db.SaveChanges();
        return device;
    }

    [Fact]
    public async Task Receipt_DuplicateKeyOnSameDevice_LosesRace_OtherDeviceMayReuseKey()
    {
        var repo = new ClientRequestReceiptRepository(DbFactory);
        var d1 = SeedDevice();
        var d2 = SeedDevice();

        Assert.True(await repo.TryInsertAsync(new ClientRequestReceipt { DeviceId = d1.Id, IdempotencyKey = "k", StatusCode = 201, ResponseBody = "first" }));
        Assert.False(await repo.TryInsertAsync(new ClientRequestReceipt { DeviceId = d1.Id, IdempotencyKey = "k", StatusCode = 500 }));
        Assert.True(await repo.TryInsertAsync(new ClientRequestReceipt { DeviceId = d2.Id, IdempotencyKey = "k" }));

        var stored = await repo.GetAsync(d1.Id, "k");
        Assert.Equal(201, stored!.StatusCode);
        Assert.Equal("first", stored.ResponseBody);
        Assert.Null(await repo.GetAsync(d1.Id, "other"));
    }

    [Fact]
    public async Task Receipt_PruneRemovesOnlyOlderEntries()
    {
        var repo = new ClientRequestReceiptRepository(DbFactory);
        var device = SeedDevice();
        var now = DateTime.UtcNow;
        await repo.TryInsertAsync(new ClientRequestReceipt { DeviceId = device.Id, IdempotencyKey = "old", CreatedAtUtc = now.AddDays(-40) });
        await repo.TryInsertAsync(new ClientRequestReceipt { DeviceId = device.Id, IdempotencyKey = "new", CreatedAtUtc = now });

        Assert.Equal(1, await repo.PruneOlderThanAsync(now.AddDays(-30)));
        Assert.Null(await repo.GetAsync(device.Id, "old"));
        Assert.NotNull(await repo.GetAsync(device.Id, "new"));
    }

    // ─────────────────────── Sync change cursor ───────────────────────

    private void SeedMeta(Guid shareId, string path, DateTime modified)
    {
        using var db = NewContext();
        db.FileMetadata.Add(new FileMetadata
        {
            Id = Guid.NewGuid(), ShareId = shareId, Path = path, Name = path,
            CreatedAt = modified, ModifiedAt = modified,
        });
        db.SaveChanges();
    }

    [Fact]
    public async Task ChangeCursor_ScopesToShareAndSubtree_WithoutMatchingSiblingPrefix()
    {
        var repo = new FileChangeCursorRepository(DbFactory);
        var share = Guid.NewGuid();
        var t0 = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        SeedMeta(share, "docs", t0);
        SeedMeta(share, "docs/a.txt", t0.AddHours(1));
        SeedMeta(share, "docs2/b.txt", t0.AddHours(5)); // sibling sharing the "docs" prefix
        SeedMeta(Guid.NewGuid(), "docs/other-share.txt", t0.AddHours(9));

        Assert.Equal(new ShareChangeState(t0.AddHours(1), 2), await repo.GetShareChangeStateAsync(share, "docs"));
        Assert.Equal(new ShareChangeState(t0.AddHours(5), 3), await repo.GetShareChangeStateAsync(share, null));
        Assert.Equal(ShareChangeState.Empty, await repo.GetShareChangeStateAsync(share, "nothing"));
    }

    // ─────────────────────── Device sync profiles ───────────────────────

    [Fact]
    public async Task SyncProfiles_CrudAndQueriesByDeviceAndUser()
    {
        var repo = new DeviceSyncProfileRepository(DbFactory);
        var device = SeedDevice();
        var otherDevice = SeedDevice();
        var userId = device.UserId;
        var share = Guid.NewGuid();
        var t0 = DateTime.UtcNow;

        var first = await repo.CreateAsync(new DeviceSyncProfile { DeviceId = device.Id, UserId = userId, ShareId = share, RelativePath = "a", CreatedAtUtc = t0 });
        await repo.CreateAsync(new DeviceSyncProfile { DeviceId = device.Id, UserId = userId, ShareId = share, RelativePath = "b", CreatedAtUtc = t0.AddMinutes(1) });
        await repo.CreateAsync(new DeviceSyncProfile { DeviceId = otherDevice.Id, UserId = otherDevice.UserId, ShareId = share, RelativePath = "a" });

        Assert.Equal(["a", "b"], (await repo.GetByDeviceAsync(device.Id)).Select(p => p.RelativePath));
        Assert.Equal(2, (await repo.GetByUserAsync(userId)).Count);

        first.Enabled = false;
        await repo.UpdateAsync(first);
        Assert.False((await repo.GetByIdAsync(first.Id))!.Enabled);

        await repo.DeleteAsync(first.Id);
        await repo.DeleteAsync(Guid.NewGuid()); // unknown id is a no-op
        Assert.Null(await repo.GetByIdAsync(first.Id));
    }

    [Fact]
    public async Task SyncProfiles_SamePathTwiceOnOneDevice_IsRejected()
    {
        var repo = new DeviceSyncProfileRepository(DbFactory);
        var device = SeedDevice();
        var share = Guid.NewGuid();
        await repo.CreateAsync(new DeviceSyncProfile { DeviceId = device.Id, UserId = device.UserId, ShareId = share, RelativePath = "a" });

        await Assert.ThrowsAsync<DbUpdateException>(() => repo.CreateAsync(
            new DeviceSyncProfile { DeviceId = device.Id, UserId = device.UserId, ShareId = share, RelativePath = "a" }));
    }

    // ─────────────────────── Cloud access grants ───────────────────────

    [Fact]
    public async Task CloudAccess_UpsertReplaceGrantsAndEffectivePermission()
    {
        var (_, _, cloudShare) = SeedLinkTargets();
        var repo = new CloudAccessRepository(DbFactory);
        var reader = Guid.NewGuid();
        var writer = Guid.NewGuid();

        cloudShare.Description = "updated";
        await repo.UpsertShareAsync(cloudShare);
        Assert.Equal("updated", (await repo.GetShareAsync(cloudShare.Id))!.Description);

        // Duplicate principals collapse to the first entry instead of violating the key.
        await repo.ReplaceGrantsAsync(cloudShare.Id,
        [
            new CloudAccessGrant { ShareId = cloudShare.Id, PrincipalId = reader, Permission = CloudAccessPermission.Read },
            new CloudAccessGrant { ShareId = cloudShare.Id, PrincipalId = reader, Permission = CloudAccessPermission.Write },
            new CloudAccessGrant { ShareId = cloudShare.Id, PrincipalId = writer, Permission = CloudAccessPermission.Write },
        ]);

        Assert.Equal(2, (await repo.GetGrantsAsync(cloudShare.Id)).Count);
        Assert.Equal(CloudAccessPermission.Read, await repo.GetEffectivePermissionAsync(cloudShare.Id, [reader]));
        Assert.Equal(CloudAccessPermission.Write, await repo.GetEffectivePermissionAsync(cloudShare.Id, [reader, writer]));
        Assert.Null(await repo.GetEffectivePermissionAsync(cloudShare.Id, [Guid.NewGuid()]));
        Assert.Null(await repo.GetEffectivePermissionAsync(cloudShare.Id, []));
        Assert.True(await repo.HasGrantAsync(cloudShare.Id, [writer]));
        Assert.False(await repo.HasGrantAsync(cloudShare.Id, []));

        await repo.DeleteShareAsync(cloudShare.Id);
        Assert.Null(await repo.GetShareAsync(cloudShare.Id));
        Assert.Empty(await repo.GetGrantsAsync(cloudShare.Id)); // cascade
    }
}

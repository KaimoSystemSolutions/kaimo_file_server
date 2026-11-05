using Kaimo_File_Server.Core.Domain;
using Kaimo_File_Server.Infrastructure.Repositories;
using Kaimo_File_Server.Infrastructure.Services;
using Kaimo_File_Server.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Kaimo_File_Server.Tests;

public sealed class CloudAccessMountGuardDatabaseTests : DatabaseTestBase
{
    private CloudAccessMountGuard Sut() => new(DbFactory, NullLogger<CloudAccessMountGuard>.Instance);

    private CloudAccessShare SeedMount(
        Guid? hostId, string? mountPath, string name = "cloud",
        CloudAccessProvisioning mode = CloudAccessProvisioning.Folder)
    {
        var dept = SeedDepartment("Dept-" + name);
        var connection = new StorageConnection
        {
            CreatedByUserId = Guid.NewGuid(),
            ProviderId = "webdav",
            Name = "conn-" + name,
            AuthorizationMode = StorageAuthorizationMode.DeviceCode,
            State = StorageConnectionState.Ready,
        };
        var share = new CloudAccessShare
        {
            ConnectionId = connection.Id,
            DepartmentId = dept.Id,
            Name = name,
            RemoteRootPath = "/",
            Provisioning = mode,
            MountShareId = hostId,
            MountPath = mountPath,
        };
        using var db = NewContext();
        db.StorageConnections.Add(connection);
        db.CloudAccessShares.Add(share);
        db.SaveChanges();
        return share;
    }

    private async Task<CloudAccessShare> Reload(Guid id)
    {
        await using var db = NewContext();
        return await db.CloudAccessShares.SingleAsync(s => s.Id == id);
    }

    [Theory]
    [InlineData("Team/Cloud", true)]
    [InlineData("team/CLOUD", true)]
    [InlineData("/Team/Cloud/", true)]
    [InlineData("Team/Cloud/sub/file.txt", true)]
    [InlineData("Team/Cloud2", false)]
    [InlineData("Team", false)]
    [InlineData("Other/Cloud", false)]
    public async Task IsReservedAsync_MatchesMountPathAndDescendantsCaseInsensitive(string path, bool expected)
    {
        var host = SeedShare("projekte");
        SeedMount(host.Id, "Team/Cloud");

        Assert.Equal(expected, await Sut().IsReservedAsync(host.Id, path));
    }

    [Fact]
    public async Task IsReservedAsync_IgnoresOtherSharesVirtualShareModeAndOrphans()
    {
        var host = SeedShare("projekte");
        var other = SeedShare("other");
        SeedMount(other.Id, "Cloud", "a");
        SeedMount(host.Id, "Legacy", "b", CloudAccessProvisioning.VirtualShare);
        SeedMount(null, null, "c");

        Assert.False(await Sut().IsReservedAsync(host.Id, "Cloud"));
        Assert.False(await Sut().IsReservedAsync(host.Id, "Legacy"));
        Assert.False(await Sut().IsReservedAsync(host.Id, ""));
    }

    [Fact]
    public async Task RenamePathAsync_RebasesMountAtAndBelowAndIsIdempotent()
    {
        var host = SeedShare("projekte");
        var exact = SeedMount(host.Id, "Team/Cloud", "a");
        var below = SeedMount(host.Id, "Team/Sub/Deep", "b");
        var unrelated = SeedMount(host.Id, "Teamwork/X", "c");

        await Sut().RenamePathAsync(host.Id, "team", "Archive/Team2", 0);
        await Sut().RenamePathAsync(host.Id, "team", "Archive/Team2", 0);

        Assert.Equal("Archive/Team2/Cloud", (await Reload(exact.Id)).MountPath);
        Assert.Equal("Archive/Team2/Sub/Deep", (await Reload(below.Id)).MountPath);
        Assert.Equal("Teamwork/X", (await Reload(unrelated.Id)).MountPath);
    }

    [Fact]
    public async Task RenamePathAsync_IntoRecycleBin_DetachesButKeepsFolderMode()
    {
        var host = SeedShare("projekte");
        var mount = SeedMount(host.Id, "Team/Cloud");

        await Sut().RenamePathAsync(host.Id, "Team", ".RECYCLE_BIN/Team", 0);

        var reloaded = await Reload(mount.Id);
        Assert.Null(reloaded.MountShareId);
        Assert.Null(reloaded.MountPath);
        Assert.Equal(CloudAccessProvisioning.Folder, reloaded.Provisioning);
    }

    [Fact]
    public async Task DetachAsync_DetachesAtAndBelowOnly()
    {
        var host = SeedShare("projekte");
        var exact = SeedMount(host.Id, "Team", "a");
        var below = SeedMount(host.Id, "Team/X/Cloud", "b");
        var sibling = SeedMount(host.Id, "Teams", "c");

        await Sut().DetachAsync(host.Id, "TEAM");

        Assert.False((await Reload(exact.Id)).IsMounted);
        Assert.False((await Reload(below.Id)).IsMounted);
        Assert.True((await Reload(sibling.Id)).IsMounted);
    }

    [Fact]
    public async Task UpsertShare_WithStaleCopy_KeepsRebasedMount()
    {
        var host = SeedShare("projekte");
        var stale = SeedMount(host.Id, "Team/Cloud");
        await Sut().RenamePathAsync(host.Id, "Team", "Team2", 0);

        stale.Name = "renamed";
        await new CloudAccessRepository(DbFactory).UpsertShareAsync(stale);

        var reloaded = await Reload(stale.Id);
        Assert.Equal("renamed", reloaded.Name);
        Assert.Equal("Team2/Cloud", reloaded.MountPath);
    }

    [Fact]
    public async Task SetMountAsync_WritesModeAndMountPoint()
    {
        var host = SeedShare("projekte");
        var share = SeedMount(null, null, mode: CloudAccessProvisioning.VirtualShare);

        await new CloudAccessRepository(DbFactory).SetMountAsync(
            share.Id, CloudAccessProvisioning.Folder, host.Id, "Cloud");

        var reloaded = await Reload(share.Id);
        Assert.True(reloaded.IsMounted);
        Assert.Equal("Cloud", reloaded.MountPath);
    }

    [Fact]
    public async Task DeletingHostShare_OrphansMount()
    {
        var host = SeedShare("projekte");
        var mount = SeedMount(host.Id, "Cloud");

        await using (var db = NewContext())
        {
            db.ShareDefinitions.Remove(await db.ShareDefinitions.SingleAsync(s => s.Id == host.Id));
            await db.SaveChangesAsync();
        }

        var reloaded = await Reload(mount.Id);
        Assert.Null(reloaded.MountShareId);
        Assert.Equal(CloudAccessProvisioning.Folder, reloaded.Provisioning);
    }
}

using Kaimo_File_Server.Core.Domain;
using Kaimo_File_Server.Core.Services.DataServices;
using Kaimo_File_Server.Infrastructure.Services;
using Kaimo_File_Server.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Kaimo_File_Server.Tests;

public sealed class CloudSyncPathUpdaterDatabaseTests : DatabaseTestBase
{
    [Fact]
    public async Task RenamePathAsync_RebasesAffectedSyncAndPersistsJson()
    {
        var share = SeedShare("docs");
        await SeedSyncsAsync(share.Id,
            ("projects/customer", "google"),
            ("unrelated", "onedrive"));
        var sut = new CloudSyncPathUpdater(DbFactory);

        await sut.RenamePathAsync(
            share.Id, "projects", "archive/current-projects");

        await using var db = NewContext();
        var persisted = await db.ShareDefinitions.SingleAsync(s => s.Id == share.Id);
        Assert.False(persisted.CloudSettings.Folders.ContainsKey("projects/customer"));
        Assert.Equal("google", persisted.CloudSettings.Folders[
            "archive/current-projects/customer"].Provider);
        Assert.Equal("onedrive",
            persisted.CloudSettings.Folders["unrelated"].Provider);
    }

    [Fact]
    public async Task RenamePathAsync_RetryIsIdempotent()
    {
        var share = SeedShare("docs");
        await SeedSyncsAsync(share.Id, ("projects", "google"));
        var sut = new CloudSyncPathUpdater(DbFactory);

        await sut.RenamePathAsync(share.Id, "projects", "archive");
        await sut.RenamePathAsync(share.Id, "projects", "archive");

        await using var db = NewContext();
        var persisted = await db.ShareDefinitions.SingleAsync(s => s.Id == share.Id);
        Assert.Single(persisted.CloudSettings.Folders);
        Assert.True(persisted.CloudSettings.Folders.ContainsKey("archive"));
    }

    [Fact]
    public async Task RuntimeStateUpdate_MergesIntoLatestShareWithoutRevertingRename()
    {
        var share = SeedShare("oldname", "/data/oldname");
        await SeedSyncsAsync(share.Id, ("projects", "google"));
        await using (var db = NewContext())
        {
            var current = await db.ShareDefinitions.SingleAsync(
                item => item.Id == share.Id);
            current.Name = "newname";
            current.Path = "/data/newname";
            current.CloudSettings.Folders["projects"].Mode = SyncMode.Pull;
            current.CloudSettings = new CloudSettings(
                new Dictionary<string, SyncedFolder>(
                    current.CloudSettings.Folders));
            await db.SaveChangesAsync();
        }
        DateTime completedAt = DateTime.UtcNow;

        bool updated = await ShareRepo().UpdateCloudSyncRuntimeStateAsync(
            share.Id,
            "projects",
            completedAt,
            new Dictionary<string, string> { ["refreshToken"] = "rotated" });

        Assert.True(updated);
        await using var verification = NewContext();
        var persisted = await verification.ShareDefinitions.SingleAsync(
            item => item.Id == share.Id);
        Assert.Equal("newname", persisted.Name);
        Assert.Equal("/data/newname", persisted.Path);
        var folder = persisted.CloudSettings.Folders["projects"];
        Assert.Equal(SyncMode.Pull, folder.Mode);
        Assert.Equal(completedAt, folder.LastSync);
        Assert.Equal("rotated", folder.Data["refreshToken"]);
    }

    [Fact]
    public async Task RenamePathAsync_RebasesFirstClassSyncDefinition_OnAncestorRename()
    {
        var share = SeedShare("docs");
        var moved = await SeedDefinitionAsync(share, "projects/customer");
        var untouched = await SeedDefinitionAsync(share, "unrelated");
        var sut = new CloudSyncPathUpdater(DbFactory);

        // Renaming the parent folder must carry the nested sync along with it.
        await sut.RenamePathAsync(share.Id, "projects", "archive/current");

        await using var db = NewContext();
        Assert.Equal("archive/current/customer",
            (await db.SyncDefinitions.SingleAsync(d => d.Id == moved.Id)).LocalPath);
        Assert.Equal("unrelated",
            (await db.SyncDefinitions.SingleAsync(d => d.Id == untouched.Id)).LocalPath);
    }

    [Fact]
    public async Task RenamePathAsync_RebasesFirstClassSyncDefinition_OnDirectRename()
    {
        var share = SeedShare("docs");
        var moved = await SeedDefinitionAsync(share, "projects");
        var sut = new CloudSyncPathUpdater(DbFactory);

        await sut.RenamePathAsync(share.Id, "projects", "archive");

        await using var db = NewContext();
        Assert.Equal("archive",
            (await db.SyncDefinitions.SingleAsync(d => d.Id == moved.Id)).LocalPath);
    }

    [Fact]
    public async Task RenamePathAsync_LeavesTombstonedDefinitionsUntouched()
    {
        var share = SeedShare("docs");
        var tombstone = await SeedDefinitionAsync(
            share, "projects", SyncDefinition.DeletedByFirstClassEditorSource);
        var sut = new CloudSyncPathUpdater(DbFactory);

        await sut.RenamePathAsync(share.Id, "projects", "archive");

        await using var db = NewContext();
        Assert.Equal("projects",
            (await db.SyncDefinitions.SingleAsync(d => d.Id == tombstone.Id)).LocalPath);
    }

    private async Task SeedSyncsAsync(
        Guid shareId,
        params (string Path, string Provider)[] syncs)
    {
        await using var db = NewContext();
        var share = await db.ShareDefinitions.SingleAsync(s => s.Id == shareId);
        share.CloudSettings = new CloudSettings(syncs.ToDictionary(
            sync => sync.Path,
            sync => new SyncedFolder(sync.Provider, new Dictionary<string, string>())));
        await db.SaveChangesAsync();
    }

    private async Task<SyncDefinition> SeedDefinitionAsync(
        ShareDefinition share, string localPath, string? migrationSource = null)
    {
        var connection = new StorageConnection
        {
            DepartmentId = share.DepartmentId,
            ProviderId = "google",
            Name = "conn-" + Guid.NewGuid().ToString("N")[..8],
            State = StorageConnectionState.Ready,
            ConcurrencyVersion = 1
        };
        var definition = new SyncDefinition
        {
            ConnectionId = connection.Id,
            LocalShareId = share.Id,
            LocalPath = localPath,
            DisplayName = "Sync " + localPath,
            MigrationSource = migrationSource
        };
        await using var db = NewContext();
        db.StorageConnections.Add(connection);
        db.SyncDefinitions.Add(definition);
        await db.SaveChangesAsync();
        return definition;
    }
}

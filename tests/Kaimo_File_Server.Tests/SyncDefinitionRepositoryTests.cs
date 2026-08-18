using Kaimo_File_Server.Core.Domain;
using Kaimo_File_Server.Infrastructure.Repositories;
using Kaimo_File_Server.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Kaimo_File_Server.Tests;

public sealed class SyncDefinitionRepositoryTests : DatabaseTestBase
{
    [Fact]
    public async Task SaveAsync_CreatesFirstClassDefinitionAndSeparateRuntime()
    {
        var share = SeedShare("first-class-save");
        var connection = new StorageConnection
        {
            DepartmentId = share.DepartmentId,
            CreatedByUserId = Guid.NewGuid(),
            ProviderId = "onedrive",
            Name = "Reusable connection",
            State = StorageConnectionState.Ready,
            ConcurrencyVersion = 1
        };
        using (var db = NewContext())
        {
            db.StorageConnections.Add(connection);
            await db.SaveChangesAsync();
        }
        var definition = new SyncDefinition
        {
            ConnectionId = connection.Id,
            LocalShareId = share.Id,
            LocalPath = "/projects/",
            RemotePath = "team/files/",
            DisplayName = "  Projects  ",
            Description = "  First-class configuration  ",
            MigrationSource = SyncDefinition.LegacyCloudSettingsSource
        };
        var repository = new SyncDefinitionRepository(DbFactory);

        await repository.SaveAsync(definition);

        var entry = Assert.Single(await repository.GetAllAsync());
        Assert.Equal("projects", entry.Definition.LocalPath);
        Assert.Equal("/team/files", entry.Definition.RemotePath);
        Assert.Equal("Projects", entry.Definition.DisplayName);
        Assert.Null(entry.Definition.MigrationSource);
        Assert.NotNull(entry.Runtime);
    }

    [Fact]
    public async Task DeleteAsync_RetainsImportedDefinitionAsAuthoritativeDisabledTombstone()
    {
        var share = SeedShare("imported-delete");
        var connection = new StorageConnection
        {
            DepartmentId = share.DepartmentId,
            CreatedByUserId = Guid.NewGuid(),
            ProviderId = "google",
            Name = "Imported connection",
            State = StorageConnectionState.Ready,
            ConcurrencyVersion = 1
        };
        var definition = new SyncDefinition
        {
            ConnectionId = connection.Id,
            LocalShareId = share.Id,
            LocalPath = "archive",
            MigrationSource = SyncDefinition.LegacyCloudSettingsSource,
            Enabled = true
        };
        using (var db = NewContext())
        {
            db.StorageConnections.Add(connection);
            db.SyncDefinitions.Add(definition);
            await db.SaveChangesAsync();
        }
        var repository = new SyncDefinitionRepository(DbFactory);

        Assert.True(await repository.DeleteAsync(definition.Id));

        using (var verification = NewContext())
        {
            var persisted = await verification.SyncDefinitions.SingleAsync();
            Assert.False(persisted.Enabled);
            Assert.Equal(SyncDefinition.DeletedByFirstClassEditorSource, persisted.MigrationSource);
            Assert.Equal(connection.Id, persisted.ConnectionId);
        }

        definition.Enabled = true;
        definition.MigrationSource = SyncDefinition.DeletedByFirstClassEditorSource;
        await repository.SaveAsync(definition);

        using var reactivatedVerification = NewContext();
        var reactivated = await reactivatedVerification.SyncDefinitions.SingleAsync();
        Assert.True(reactivated.Enabled);
        Assert.Null(reactivated.MigrationSource);
    }

    [Fact]
    public async Task RuntimeUpdates_DoNotRewriteConfigurationAndRetainLastSuccessOnFailure()
    {
        var share = SeedShare("sync-runtime");
        var connection = new StorageConnection
        {
            DepartmentId = share.DepartmentId,
            CreatedByUserId = Guid.NewGuid(),
            ProviderId = "google",
            Name = $"Runtime connection {Guid.NewGuid():N}",
            State = StorageConnectionState.Ready,
            ConcurrencyVersion = 1
        };
        var definition = new SyncDefinition
        {
            ConnectionId = connection.Id,
            LocalShareId = share.Id,
            LocalPath = "projects",
            RemotePath = "/remote",
            Description = "Configuration must remain unchanged"
        };
        using (var db = NewContext())
        {
            db.StorageConnections.Add(connection);
            db.SyncDefinitions.Add(definition);
            await db.SaveChangesAsync();
        }
        var repository = new SyncDefinitionRepository(DbFactory);
        var completedAt = new DateTime(2026, 8, 18, 12, 0, 0, DateTimeKind.Utc);
        var failedAt = completedAt.AddMinutes(5);

        await repository.MarkCompletedAsync(definition.Id, completedAt);
        await repository.MarkFailedAsync(definition.Id, failedAt, "invalid_grant");

        using var verification = NewContext();
        var runtime = await verification.SyncDefinitionRuntimes.SingleAsync();
        var persistedDefinition = await verification.SyncDefinitions.SingleAsync();
        Assert.Equal(completedAt, runtime.LastSuccessfulRunAtUtc);
        Assert.Equal(failedAt, runtime.LastRunAtUtc);
        Assert.Equal("invalid_grant", runtime.LastErrorCode);
        Assert.Equal("Configuration must remain unchanged", persistedDefinition.Description);
    }
}

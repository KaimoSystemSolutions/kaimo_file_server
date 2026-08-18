using Kaimo_File_Server.Core.Domain;
using Kaimo_File_Server.Core.Services;
using Kaimo_File_Server.Core.Services.DataServices;
using Kaimo_File_Server.Infrastructure.Services;
using Kaimo_File_Server.Tests.Infrastructure;
using Kaimo_File_Server.Web.Services;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Kaimo_File_Server.Tests;

public sealed class LegacyCloudSyncMigrationTests : DatabaseTestBase
{
    [Fact]
    public async Task EnsureMigratedAsync_CreatesOneProtectedConnectionAndDefinitionPerMapping()
    {
        var user = SeedUser("scheduler");
        var share = SeedShare("docs", departmentId: Guid.NewGuid());
        using (var db = NewContext())
        {
            var persisted = await db.ShareDefinitions.SingleAsync(item => item.Id == share.Id);
            persisted.CloudSettings.Folders["projects"] = new SyncedFolder(
                "google",
                new Dictionary<string, string>
                {
                    ["refreshToken"] = "sensitive-refresh-token",
                    ["scope"] = "drive.file"
                },
                "/team")
            {
                DisplayName = "Team drive",
                Mode = SyncMode.Pull,
                LastSync = new DateTime(2026, 8, 18, 10, 0, 0, DateTimeKind.Utc),
                Schedule = new CloudSyncSchedule
                {
                    IsEnabled = true,
                    RunAsUsername = user.Username,
                    ActiveSlots = [CloudSyncSchedule.ToSlot(DayOfWeek.Tuesday, 10)]
                },
                AdvancedSettings = new CloudSyncAdvancedSettings
                {
                    MaxDownloadBytesPerSecond = 1024
                }
            };
            persisted.CloudSettings = new CloudSettings(
                new Dictionary<string, SyncedFolder>(persisted.CloudSettings.Folders));
            await db.SaveChangesAsync();
        }

        var vault = new DataProtectionCredentialVault(new EphemeralDataProtectionProvider());
        var sut = new LegacyCloudSyncMigrationService(
            DbFactory,
            vault,
            NullLogger<LegacyCloudSyncMigrationService>.Instance);

        await sut.EnsureMigratedAsync();
        await sut.EnsureMigratedAsync();

        using var verification = NewContext();
        var definition = await verification.SyncDefinitions.SingleAsync();
        var connection = await verification.StorageConnections.SingleAsync();
        var runtime = await verification.SyncDefinitionRuntimes.SingleAsync();
        Assert.Equal(connection.Id, definition.ConnectionId);
        Assert.Equal(share.Id, definition.LocalShareId);
        Assert.Equal("projects", definition.LocalPath);
        Assert.Equal("/team", definition.RemotePath);
        Assert.Equal(user.Id, definition.RunAsUserId);
        Assert.True(definition.Schedule.IsEnabled);
        Assert.Equal(1024, definition.AdvancedSettings.MaxDownloadBytesPerSecond);
        Assert.Equal(share.DepartmentId, connection.DepartmentId);
        Assert.DoesNotContain("sensitive-refresh-token", connection.EncryptedCredentialPayload);
        var credentials = vault.UnprotectConnectionCredentials(connection);
        Assert.Equal("sensitive-refresh-token", credentials["refreshToken"]);
        Assert.Equal(new DateTime(2026, 8, 18, 10, 0, 0, DateTimeKind.Utc),
            runtime.LastSuccessfulRunAtUtc);

        // Package 5 keeps the source as a read-only rollback fallback. Cleanup
        // is intentionally a later, explicitly verified migration.
        Assert.True((await verification.ShareDefinitions.SingleAsync())
            .CloudSettings.Folders.ContainsKey("projects"));
    }

    [Fact]
    public async Task EnsureMigratedAsync_DisablesImportedDefinitionWhenLegacyMappingWasRemoved()
    {
        var share = SeedShare("docs");
        using (var db = NewContext())
        {
            var persisted = await db.ShareDefinitions.SingleAsync(item => item.Id == share.Id);
            persisted.CloudSettings.Folders["projects"] = new SyncedFolder(
                "onedrive", new Dictionary<string, string> { ["refreshToken"] = "token" });
            persisted.CloudSettings = new CloudSettings(
                new Dictionary<string, SyncedFolder>(persisted.CloudSettings.Folders));
            await db.SaveChangesAsync();
        }
        var sut = new LegacyCloudSyncMigrationService(
            DbFactory,
            new DataProtectionCredentialVault(new EphemeralDataProtectionProvider()),
            NullLogger<LegacyCloudSyncMigrationService>.Instance);
        await sut.EnsureMigratedAsync();

        using (var db = NewContext())
        {
            var persisted = await db.ShareDefinitions.SingleAsync(item => item.Id == share.Id);
            persisted.CloudSettings.Folders.Clear();
            persisted.CloudSettings = new CloudSettings(new Dictionary<string, SyncedFolder>());
            await db.SaveChangesAsync();
        }
        await sut.EnsureMigratedAsync();

        using var verification = NewContext();
        Assert.False((await verification.SyncDefinitions.SingleAsync()).Enabled);
        Assert.Single(await verification.StorageConnections.ToListAsync());
    }
}

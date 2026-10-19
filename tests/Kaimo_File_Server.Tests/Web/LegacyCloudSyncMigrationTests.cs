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
        Assert.DoesNotContain("sensitive-refresh-token", connection.EncryptedCredentialPayload);
        var credentials = vault.UnprotectConnectionCredentials(connection);
        Assert.Equal("sensitive-refresh-token", credentials["refreshToken"]);
        Assert.Equal(new DateTime(2026, 8, 18, 10, 0, 0, DateTimeKind.Utc),
            runtime.LastSuccessfulRunAtUtc);

        // Package 5 keeps the source as a read-only rollback fallback. Cleanup
        // is intentionally a later, explicitly verified migration. The secret
        // itself is scrubbed: it must only survive inside the vault.
        var legacyFolder = (await verification.ShareDefinitions.SingleAsync())
            .CloudSettings.Folders["projects"];
        Assert.False(legacyFolder.Data.ContainsKey("refreshToken"));
        Assert.Equal("drive.file", legacyFolder.Data["scope"]);
    }

    [Fact]
    public async Task EnsureMigratedAsync_RepeatedRunsAfterScrubKeepVaultCredentials()
    {
        var share = SeedShare("docs");
        await SetLegacyFolderAsync(share.Id, "projects", new SyncedFolder(
            "onedrive",
            new Dictionary<string, string> { ["refreshToken"] = "vaulted", ["scope"] = "Files.ReadWrite" },
            "/remote"));
        var vault = new DataProtectionCredentialVault(new EphemeralDataProtectionProvider());
        var sut = NewSut(vault);
        await sut.EnsureMigratedAsync();
        long versionAfterImport;
        using (var db = NewContext())
            versionAfterImport = (await db.StorageConnections.SingleAsync()).ConcurrencyVersion;

        // The scrub changes the JSON; that must neither look like a legacy edit
        // (checksum churn) nor wipe the vaulted token on a later run.
        await sut.EnsureMigratedAsync();
        await sut.EnsureMigratedAsync();

        using var verification = NewContext();
        var connection = await verification.StorageConnections.SingleAsync();
        Assert.Equal(versionAfterImport, connection.ConcurrencyVersion);
        Assert.Equal("vaulted", vault.UnprotectConnectionCredentials(connection)["refreshToken"]);
        Assert.DoesNotContain("vaulted",
            (await verification.ShareDefinitions.SingleAsync()).CloudSettings.Serialize());
    }

    [Fact]
    public async Task EnsureMigratedAsync_LegacyEditAfterScrubPreservesVaultedToken()
    {
        var share = SeedShare("docs");
        await SetLegacyFolderAsync(share.Id, "projects", new SyncedFolder(
            "onedrive", new Dictionary<string, string> { ["refreshToken"] = "vaulted" }, "/old"));
        var vault = new DataProtectionCredentialVault(new EphemeralDataProtectionProvider());
        var sut = NewSut(vault);
        await sut.EnsureMigratedAsync();

        using (var db = NewContext())
        {
            var persisted = await db.ShareDefinitions.SingleAsync(item => item.Id == share.Id);
            persisted.CloudSettings.Folders["projects"].RemotePath = "/new";
            db.Entry(persisted).Property(item => item.CloudSettings).IsModified = true;
            await db.SaveChangesAsync();
        }
        await sut.EnsureMigratedAsync();

        using var verification = NewContext();
        Assert.Equal("/new", (await verification.SyncDefinitions.SingleAsync()).RemotePath);
        var connection = await verification.StorageConnections.SingleAsync();
        Assert.Equal("vaulted", vault.UnprotectConnectionCredentials(connection)["refreshToken"]);
    }

    [Fact]
    public async Task EnsureMigratedAsync_ImportsFreshLegacyGrantForExistingMapping()
    {
        var share = SeedShare("docs");
        await SetLegacyFolderAsync(share.Id, "projects", new SyncedFolder(
            "google", new Dictionary<string, string> { ["refreshToken"] = "first", ["scope"] = "drive" }));
        var vault = new DataProtectionCredentialVault(new EphemeralDataProtectionProvider());
        var sut = NewSut(vault);
        await sut.EnsureMigratedAsync();

        // A legacy OAuth callback re-connects the same folder with the same
        // scope: only the secret differs, which the checksum deliberately ignores.
        await SetLegacyFolderAsync(share.Id, "projects", new SyncedFolder(
            "google", new Dictionary<string, string> { ["refreshToken"] = "second", ["scope"] = "drive" }));
        await sut.EnsureMigratedAsync();

        using var verification = NewContext();
        var connection = await verification.StorageConnections.SingleAsync();
        Assert.Equal("second", vault.UnprotectConnectionCredentials(connection)["refreshToken"]);
        Assert.False((await verification.ShareDefinitions.SingleAsync())
            .CloudSettings.Folders["projects"].Data.ContainsKey("refreshToken"));
    }

    [Fact]
    public async Task EnsureMigratedAsync_ScrubsSecretsOfMappingsOwnedByFirstClassEditor()
    {
        var share = SeedShare("docs");
        await SetLegacyFolderAsync(share.Id, "projects", new SyncedFolder(
            "onedrive", new Dictionary<string, string> { ["refreshToken"] = "first" }));
        var sut = NewSut(new DataProtectionCredentialVault(new EphemeralDataProtectionProvider()));
        await sut.EnsureMigratedAsync();
        using (var db = NewContext())
        {
            (await db.SyncDefinitions.SingleAsync()).MigrationSource = null;
            await db.SaveChangesAsync();
        }
        await SetLegacyFolderAsync(share.Id, "projects", new SyncedFolder(
            "onedrive", new Dictionary<string, string> { ["refreshToken"] = "stale-copy" }));

        await sut.EnsureMigratedAsync();

        using var verification = NewContext();
        Assert.DoesNotContain("stale-copy",
            (await verification.ShareDefinitions.SingleAsync()).CloudSettings.Serialize());
    }

    private LegacyCloudSyncMigrationService NewSut(DataProtectionCredentialVault vault)
        => new(DbFactory, vault, NullLogger<LegacyCloudSyncMigrationService>.Instance);

    private async Task SetLegacyFolderAsync(Guid shareId, string path, SyncedFolder folder)
    {
        using var db = NewContext();
        var persisted = await db.ShareDefinitions.SingleAsync(item => item.Id == shareId);
        persisted.CloudSettings = new CloudSettings(
            new Dictionary<string, SyncedFolder> { [path] = folder });
        await db.SaveChangesAsync();
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

    [Fact]
    public async Task EnsureMigratedAsync_DoesNotOverwriteDefinitionOwnedByFirstClassEditor()
    {
        var share = SeedShare("authoritative-sync");
        using (var db = NewContext())
        {
            var persisted = await db.ShareDefinitions.SingleAsync(item => item.Id == share.Id);
            persisted.CloudSettings.Folders["projects"] = new SyncedFolder(
                "onedrive", new Dictionary<string, string> { ["refreshToken"] = "original" }, "/legacy");
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
            var definition = await db.SyncDefinitions.SingleAsync();
            definition.MigrationSource = null;
            definition.RemotePath = "/first-class";
            var persisted = await db.ShareDefinitions.SingleAsync(item => item.Id == share.Id);
            persisted.CloudSettings.Folders["projects"].RemotePath = "/changed-legacy";
            persisted.CloudSettings = new CloudSettings(
                new Dictionary<string, SyncedFolder>(persisted.CloudSettings.Folders));
            await db.SaveChangesAsync();
        }

        await sut.EnsureMigratedAsync();

        using var verification = NewContext();
        Assert.Equal("/first-class", (await verification.SyncDefinitions.SingleAsync()).RemotePath);
        Assert.Single(await verification.StorageConnections.ToListAsync());
    }
}

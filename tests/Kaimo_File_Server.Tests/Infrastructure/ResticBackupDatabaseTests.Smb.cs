using Xunit;
using System.Text.Json;
using Kaimo_File_Server.Core.Domain;
using Kaimo_File_Server.Core.Domain.Backup;
using Kaimo_File_Server.Core.Domain.Identity;
using Kaimo_File_Server.Core.Security;
using Kaimo_File_Server.Core.Services;
using Kaimo_File_Server.Infrastructure.Backup.Restic;
using Kaimo_File_Server.Infrastructure.ExternalStorage;
using Microsoft.Extensions.Configuration;
using Moq;

namespace Kaimo_File_Server.Tests;

public partial class ResticBackupDatabaseTests
{
    // ─────────────── SMB repositories (External Storage connection via rclone) ───────────────

    private const string SmbPassword = "smb-Secret#123";

    private (ResticTargetResolver Resolver, ICredentialVault Vault, string TempRoot) SmbResolver()
    {
        var vault = new Web.Services.DataProtectionCredentialVault(
            Microsoft.AspNetCore.DataProtection.DataProtectionProvider.Create("KaimoSmbTests"));
        var tempRoot = Path.Combine(Path.GetTempPath(), "kaimo-smb-" + Guid.NewGuid().ToString("N"));
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Backup:Restic:TempDirectory"] = tempRoot }).Build();
        return (new ResticTargetResolver(DbFactory, vault, config), vault, tempRoot);
    }

    private async Task<StorageConnection> SeedSmbConnectionAsync(
        ICredentialVault vault, StorageConnectionState state = StorageConnectionState.Ready, string providerId = "smb")
    {
        var connection = new StorageConnection
        {
            ProviderId = providerId,
            Name = $"NAS {providerId} {Guid.NewGuid():N}",
            AuthorizationMode = StorageAuthorizationMode.UsernamePassword,
            SettingsJson = JsonSerializer.Serialize(new SmbConnectionSettings("nas.example.com", Domain: "CORP")),
            State = state,
        };
        connection.EncryptedCredentialPayload = vault.ProtectConnectionCredentials(connection,
            new Dictionary<string, string> { ["username"] = "backup", ["password"] = SmbPassword });
        await using var db = NewContext();
        db.StorageConnections.Add(connection);
        await db.SaveChangesAsync();
        return connection;
    }

    private static BackupRepository SmbRepository(Guid connectionId) => new()
    {
        Name = "NAS",
        Backend = BackupBackend.Smb,
        State = BackupRepositoryState.Active,
        SettingsJson = new ResticRepositorySettings { StorageConnectionId = connectionId, Path = "backup/kaimo-repo" }.Serialize(),
    };

    [Fact]
    public async Task Smb_ResolvesToAnRcloneRemote_WithObscuredPassword_AndCleansUp()
    {
        var (resolver, vault, tempRoot) = SmbResolver();
        var connection = await SeedSmbConnectionAsync(vault);
        var repo = SmbRepository(connection.Id);

        string configPath;
        using (var target = await resolver.ResolveAsync(repo, new ResticBackendSecrets(), "pw", default))
        {
            Assert.Equal("rclone:kaimo:backup/kaimo-repo", target.RepositoryUrl);
            configPath = target.Environment["RCLONE_CONFIG"];
            var config = await File.ReadAllTextAsync(configPath);
            Assert.StartsWith("[kaimo]\ntype = smb\n", config);
            Assert.Contains("host = nas.example.com\n", config);
            Assert.Contains("user = backup\n", config);
            Assert.Contains("domain = CORP\n", config);
            Assert.Matches("\npass = [A-Za-z0-9_-]+\n", config);
            Assert.DoesNotContain(SmbPassword, config);
            Assert.DoesNotContain(target.Environment.Values, v => v.Contains(SmbPassword));
            if (!OperatingSystem.IsWindows())
                Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(configPath));
        }

        Assert.False(File.Exists(configPath));
        Assert.False(Directory.EnumerateFileSystemEntries(tempRoot).Any());
    }

    [Fact]
    public async Task Smb_DisabledOrForeignConnection_IsRejected()
    {
        var (resolver, vault, _) = SmbResolver();
        var disabled = await SeedSmbConnectionAsync(vault, StorageConnectionState.Disabled);
        var ssh = await SeedSmbConnectionAsync(vault, providerId: "sftp");

        async Task<string> CodeAsync(Guid connectionId)
            => (await Assert.ThrowsAsync<ResticException>(() =>
                resolver.ResolveAsync(SmbRepository(connectionId), new ResticBackendSecrets(), "pw", default))).Code;

        Assert.Equal("connection_disabled", await CodeAsync(disabled.Id));
        Assert.Equal("connection_invalid", await CodeAsync(ssh.Id));
        Assert.Equal("connection_missing", await CodeAsync(Guid.NewGuid()));
    }

    [Fact]
    public async Task ListSmbConnections_ReturnsOnlySmbConnections()
    {
        var (_, vault, _) = SmbResolver();
        var smb = await SeedSmbConnectionAsync(vault);
        await SeedSmbConnectionAsync(vault, providerId: "sftp");
        _auth.Setup(a => a.HasGlobalPermissionAsync(It.IsAny<UserContext>(), ManagementPermission.ManageBackupRepositories)).ReturnsAsync(true);

        var connections = await Catalog().ListSmbConnectionsAsync(Actor);

        Assert.Equal(smb.Id, Assert.Single(connections).Id);
    }
}

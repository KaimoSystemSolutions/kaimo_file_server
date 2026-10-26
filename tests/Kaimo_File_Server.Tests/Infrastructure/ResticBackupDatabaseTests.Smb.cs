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

    // smbclient -d 5 output of the transport probe (captured against Samba 4.22, smbclient 4.19).
    private const string SmbProbeEncrypted =
        "Domain=[SAMBA] OS=[] Server=[]\n tconx ok\nsmb2_signing_encrypt_pdu: Encrypted SMB2 message\n";
    private const string SmbProbePlain = "Domain=[SAMBA] OS=[] Server=[]\n tconx ok\n";

    private (ResticTargetResolver Resolver, ICredentialVault Vault, string TempRoot) SmbResolver(
        ProtocolCommandResult? probe = null, List<IReadOnlyList<string>>? probeCalls = null)
    {
        var vault = new Web.Services.DataProtectionCredentialVault(
            Microsoft.AspNetCore.DataProtection.DataProtectionProvider.Create("KaimoSmbTests"));
        var tempRoot = Path.Combine(Path.GetTempPath(), "kaimo-smb-" + Guid.NewGuid().ToString("N"));
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Backup:Restic:TempDirectory"] = tempRoot }).Build();
        var runner = new Mock<IProtocolCommandRunner>();
        runner.Setup(r => r.RunAsync("smbclient", It.IsAny<IReadOnlyList<string>>(), It.IsAny<CancellationToken>(), It.IsAny<bool>()))
            .Callback((string _, IReadOnlyList<string> arguments, CancellationToken _, bool _) => probeCalls?.Add(arguments.ToArray()))
            .ReturnsAsync(probe ?? new ProtocolCommandResult(0, "Current directory is \\\\nas.example.com\\backup\\\n", SmbProbeEncrypted));
        return (new ResticTargetResolver(DbFactory, vault, config, smbCommandRunner: runner.Object), vault, tempRoot);
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
    public async Task Smb_ProbesTheShare_WithSmb3_WithoutRequestingEncryption()
    {
        var calls = new List<IReadOnlyList<string>>();
        var (resolver, vault, _) = SmbResolver(probeCalls: calls);
        var connection = await SeedSmbConnectionAsync(vault);

        using (await resolver.ResolveAsync(SmbRepository(connection.Id), new ResticBackendSecrets(), "pw", default)) { }

        // Only a client that leaves encryption to the server shows whether the server requires it.
        var arguments = Assert.Single(calls);
        Assert.Contains("//nas.example.com/backup", arguments);
        Assert.Contains("--client-protection=off", arguments);
        Assert.DoesNotContain("--client-protection=encrypt", arguments);
        Assert.Contains("client min protocol=SMB3", arguments);
        Assert.Equal("5", arguments[arguments.ToList().IndexOf("-d") + 1]);
        Assert.DoesNotContain(arguments, a => a.Contains(SmbPassword, StringComparison.Ordinal));
        Assert.False(File.Exists(arguments[arguments.ToList().IndexOf("-A") + 1]));
    }

    [Fact]
    public async Task Smb_ServerNotRequiringEncryption_IsRefused_AndLeavesNoConfig()
    {
        var (resolver, vault, tempRoot) = SmbResolver(new ProtocolCommandResult(0, "Current directory is \\\\nas\\backup\\\n", SmbProbePlain));
        var connection = await SeedSmbConnectionAsync(vault);

        var ex = await Assert.ThrowsAsync<ResticException>(() =>
            resolver.ResolveAsync(SmbRepository(connection.Id), new ResticBackendSecrets(), "pw", default));

        Assert.Equal("smb_encryption_not_enforced", ex.Code);
        Assert.False(Directory.Exists(tempRoot) && Directory.EnumerateFileSystemEntries(tempRoot).Any());
    }

    [Theory]
    [InlineData("tree connect failed: NT_STATUS_BAD_NETWORK_NAME", "smb_share_not_found")]
    [InlineData("session setup failed: NT_STATUS_LOGON_FAILURE", "access_denied")]
    [InlineData("tree connect failed: NT_STATUS_ACCESS_DENIED", "access_denied")]
    [InlineData("do_connect: Connection to nas failed (Error NT_STATUS_NOT_FOUND)", "repo_unreachable")]
    [InlineData("protocol negotiation failed: NT_STATUS_CONNECTION_RESET", "repo_unreachable")]
    // Debug level 5 prints unrelated "Permission denied" lines in every run; they must not count.
    [InlineData("Unable to open /var/lib/samba/private/secrets.tdb: Permission denied\ndo_connect: Connection to nas failed (Error NT_STATUS_NOT_FOUND)", "repo_unreachable")]
    [InlineData("session setup failed: NT_STATUS_ACCOUNT_LOCKED_OUT", "access_denied")]
    public async Task Smb_ProbeFailures_MapToBackupErrorCodes(string stderr, string code)
    {
        // The marker must not rescue a failed connection.
        var (resolver, vault, _) = SmbResolver(new ProtocolCommandResult(1, "", SmbProbeEncrypted + stderr));
        var connection = await SeedSmbConnectionAsync(vault);

        var ex = await Assert.ThrowsAsync<ResticException>(() =>
            resolver.ResolveAsync(SmbRepository(connection.Id), new ResticBackendSecrets(), "pw", default));

        Assert.Equal(code, ex.Code);
    }

    [Fact]
    public void Smb_ProbeErrorCodes_AreLocalizedInBothLanguages()
    {
        var de = System.Globalization.CultureInfo.GetCultureInfo("de");
        foreach (var key in new[] { "Web_Backup_Error_smb_encryption_not_enforced", "Web_Backup_Repos_SmbEncryptionHint" })
        {
            var english = Kaimo_File_Server.Core.Language.Resources.ResourceManager.GetString(key, System.Globalization.CultureInfo.InvariantCulture);
            var german = Kaimo_File_Server.Core.Language.Resources.ResourceManager.GetString(key, de);
            Assert.False(string.IsNullOrWhiteSpace(english), key);
            Assert.NotEqual(english, german);
        }
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

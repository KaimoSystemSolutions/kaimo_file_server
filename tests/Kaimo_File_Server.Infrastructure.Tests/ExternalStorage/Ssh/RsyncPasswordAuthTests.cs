using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using Kaimo_File_Server.Core.Domain;
using Kaimo_File_Server.Core.Security.Credentials;
using Kaimo_File_Server.Core.Services.ExternalStorage;
using Microsoft.Extensions.Configuration;
using Moq;
using Xunit;
using Kaimo_File_Server.Infrastructure.ExternalStorage.Protocols;
using Kaimo_File_Server.Infrastructure.ExternalStorage.Ssh;

namespace Kaimo_File_Server.Infrastructure.Tests.ExternalStorage.Ssh;

public sealed class RsyncPasswordAuthTests : IDisposable
{
    private const string Password = "s3cret'pw with spaces";

    private readonly string _temporaryRoot = Path.Combine(
        Path.GetTempPath(), $"kaimo-rsync-password-tests-{Guid.NewGuid():N}");

    public RsyncPasswordAuthTests() => Directory.CreateDirectory(_temporaryRoot);

    public void Dispose() => Directory.Delete(_temporaryRoot, recursive: true);

    [Fact]
    public void Provider_OffersKeyAndPassword()
    {
        var provider = new RsyncSshStorageConnectionProvider(Mock.Of<IRsyncProcessRunner>());

        Assert.Equal(
            [StorageAuthorizationMode.UsernamePassword, StorageAuthorizationMode.SshKey],
            provider.AuthorizationModes.Order());
    }

    [Fact]
    public async Task TestAsync_MaterializesAskpassOnlyForTheOperation()
    {
        string? askpass = null;
        string? storedPassword = null;
        var runner = new Mock<IRsyncProcessRunner>();
        runner.Setup(candidate => candidate.TestSshAsync(
                It.IsAny<RsyncSshConnectionSettings>(), It.IsAny<CancellationToken>()))
            .Returns<RsyncSshConnectionSettings, CancellationToken>((settings, _) =>
            {
                askpass = settings.AskpassPath;
                Assert.NotNull(askpass);
                Assert.True(File.Exists(askpass));
                Assert.DoesNotContain(Password, File.ReadAllText(askpass));
                storedPassword = File.ReadAllText(Path.Combine(Path.GetDirectoryName(askpass)!, "ssh-password"));
                return Task.FromResult(true);
            });

        var health = await new RsyncSshStorageConnectionProvider(runner.Object, Vault())
            .TestAsync(PasswordConnection());

        Assert.True(health.IsHealthy);
        Assert.Equal(Password, storedPassword);
        Assert.False(Directory.Exists(Path.GetDirectoryName(askpass!)));
    }

    [Fact]
    public async Task OpenSession_KeepsAskpassUntilDisposed()
    {
        string? askpass = null;
        var runner = new Mock<IRsyncProcessRunner>();
        runner.Setup(candidate => candidate.RunRsyncAsync(
                It.IsAny<RsyncSshConnectionSettings>(), It.IsAny<OptimizedSyncRequest>(), It.IsAny<CancellationToken>()))
            .Callback<RsyncSshConnectionSettings, OptimizedSyncRequest, CancellationToken>(
                (settings, _, _) => askpass = settings.AskpassPath)
            .ReturnsAsync([]);
        var session = await new RsyncSshStorageConnectionProvider(runner.Object, Vault())
            .OpenSessionAsync(PasswordConnection());

        await session.OptimizedSync!.SynchronizeAsync(null!);

        Assert.NotNull(askpass);
        Assert.True(File.Exists(askpass));
        await session.DisposeAsync();
        Assert.False(Directory.Exists(Path.GetDirectoryName(askpass)));
    }

    [Fact]
    public async Task PasswordMode_WithoutStoredPassword_IsInvalidConfiguration()
    {
        var connection = PasswordConnection();
        connection.EncryptedCredentialPayload = null;
        var runner = new Mock<IRsyncProcessRunner>(MockBehavior.Strict);

        var result = await new RsyncSshStorageConnectionProvider(runner.Object, Vault()).TestAsync(connection);

        Assert.Equal(StorageConnectionHealthState.InvalidConfiguration, result.State);
        Assert.Equal("ssh_password_missing", result.Code);
    }

    [Fact]
    public async Task KeyMode_GetsNoAskpass()
    {
        string privateKey = WriteFile("id_key", "private-key-placeholder");
        var connection = PasswordConnection(privateKey);
        connection.AuthorizationMode = StorageAuthorizationMode.SshKey;
        RsyncSshConnectionSettings? seen = null;
        var runner = new Mock<IRsyncProcessRunner>();
        runner.Setup(candidate => candidate.TestSshAsync(
                It.IsAny<RsyncSshConnectionSettings>(), It.IsAny<CancellationToken>()))
            .Callback<RsyncSshConnectionSettings, CancellationToken>((settings, _) => seen = settings)
            .ReturnsAsync(true);

        await new RsyncSshStorageConnectionProvider(runner.Object, Vault()).TestAsync(connection);

        Assert.NotNull(seen);
        Assert.Null(seen.AskpassPath);
        Assert.Equal(privateKey, seen.PrivateKeySecretReference);
    }

    [Fact]
    public void StoredSettings_CannotInjectAskpassHelper()
    {
        // The helper is executed by ssh, so a value in the settings JSON must never reach it.
        var parsed = ProtocolConnectionSettings.Parse<RsyncSshConnectionSettings>(
            """
            {"Host":"h","Port":22,"Username":"u","RemoteRoot":"/","ExpectedHostKeySha256":"x",
             "PrivateKeySecretReference":null,"KnownHostsSecretReference":"/k","AskpassPath":"/tmp/evil"}
            """, "test");

        Assert.Null(parsed.AskpassPath);
        Assert.DoesNotContain("AskpassPath", JsonSerializer.Serialize(parsed with { AskpassPath = "/tmp/x" }));
    }

    [Fact]
    public void RemoteShell_PasswordModeHasNoKeyAndNoBatchMode()
    {
        string shell = RsyncProcessRunner.BuildRemoteShell(ShellSettings() with { AskpassPath = "/tmp/x/askpass" });

        Assert.DoesNotContain("BatchMode", shell);
        Assert.DoesNotContain("-i ", shell);
        Assert.DoesNotContain("/tmp/x/askpass", shell);
        Assert.Contains("StrictHostKeyChecking=yes", shell);
        Assert.Contains("UserKnownHostsFile=/secrets/known_hosts", shell);
        Assert.Contains("PubkeyAuthentication=no", shell);
        Assert.EndsWith("-p 2222", shell);
    }

    [Fact]
    public void RemoteShell_KeyModeIsUnchanged()
    {
        string shell = RsyncProcessRunner.BuildRemoteShell(ShellSettings() with { PrivateKeySecretReference = "/secrets/id_key" });

        Assert.Contains("BatchMode=yes", shell);
        Assert.Contains("PasswordAuthentication=no", shell);
        Assert.Contains("-i /secrets/id_key", shell);
    }

    [Fact]
    public void SshArguments_PasswordModeMirrorsRemoteShell()
    {
        var settings = ShellSettings() with { AskpassPath = "/tmp/x/askpass" };

        var arguments = RsyncProcessRunner.BuildSshArguments(settings);

        Assert.Equal("ssh " + string.Join(' ', arguments), RsyncProcessRunner.BuildRemoteShell(settings));
    }

    [Fact]
    public void ApplyAskpass_ForcesHelperOnlyInPasswordMode()
    {
        var password = new ProcessStartInfo("ssh");
        var key = new ProcessStartInfo("ssh");

        RsyncProcessRunner.ApplyAskpass(password, "/tmp/x/askpass");
        RsyncProcessRunner.ApplyAskpass(key, null);

        Assert.Equal("/tmp/x/askpass", password.Environment["SSH_ASKPASS"]);
        Assert.Equal("force", password.Environment["SSH_ASKPASS_REQUIRE"]);
        Assert.False(key.Environment.ContainsKey("SSH_ASKPASS_REQUIRE"));
    }

    [Fact]
    public void RemoteShell_PasswordModeDetectsDeadPeersAndStaysResticSafe()
    {
        string shell = RsyncProcessRunner.BuildPasswordRemoteShell(ShellSettings());

        Assert.Contains("-o ServerAliveInterval=30 -o ServerAliveCountMax=4", shell);
        // restic splits its -o values at commas.
        Assert.DoesNotContain(",", shell);
    }

    [Theory]
    [InlineData("backup@nas: Permission denied (publickey,password).", "ssh_authentication_failed")]
    [InlineData("ssh_askpass: exec(/tmp/x/askpass): Permission denied\nbackup@nas: Permission denied (password).", "ssh_askpass_unavailable")]
    [InlineData("ssh: connect to host nas port 22: Connection refused", null)]
    [InlineData("", null)]
    public void SshFailureCode_ClassifiesTheClientMessage(string stderr, string? expected)
        => Assert.Equal(expected, RsyncProcessRunner.SshFailureCode(stderr));

    [Fact]
    public async Task TestAsync_ReportsTheFailureCodeOfTheRunner()
    {
        var runner = new Mock<IRsyncProcessRunner>();
        runner.Setup(candidate => candidate.TestSshAsync(
                It.IsAny<RsyncSshConnectionSettings>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new ProtocolConfigurationException("ssh_authentication_failed", "rejected"));

        var health = await new RsyncSshStorageConnectionProvider(runner.Object, Vault()).TestAsync(PasswordConnection());

        Assert.False(health.IsHealthy);
        Assert.Equal("ssh_authentication_failed", health.Code);
    }

    [Fact]
    public async Task WithTimeout_ReportsAStalledProbeAsFailed()
    {
        bool result = await RsyncProcessRunner.WithTimeoutAsync(
            async token => { await Task.Delay(Timeout.Infinite, token); return true; },
            TimeSpan.FromMilliseconds(20),
            CancellationToken.None);

        Assert.False(result);
    }

    [Fact]
    public async Task WithTimeout_PropagatesCallerCancellation()
    {
        using var caller = new CancellationTokenSource();
        caller.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => RsyncProcessRunner.WithTimeoutAsync(
            async token => { await Task.Delay(Timeout.Infinite, token); return true; },
            TimeSpan.FromMinutes(1),
            caller.Token));
    }

    [Fact]
    public async Task OpenSession_PlacesAskpassInTheConfiguredTempDirectory()
    {
        string configuredRoot = Path.Combine(_temporaryRoot, "configured");
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Backup:Restic:TempDirectory"] = configuredRoot })
            .Build();
        string? askpass = null;
        var runner = new Mock<IRsyncProcessRunner>();
        runner.Setup(candidate => candidate.RunRsyncAsync(
                It.IsAny<RsyncSshConnectionSettings>(), It.IsAny<OptimizedSyncRequest>(), It.IsAny<CancellationToken>()))
            .Callback<RsyncSshConnectionSettings, OptimizedSyncRequest, CancellationToken>(
                (settings, _, _) => askpass = settings.AskpassPath)
            .ReturnsAsync([]);

        await using (var session = await new RsyncSshStorageConnectionProvider(runner.Object, Vault(), configuration: configuration)
            .OpenSessionAsync(PasswordConnection()))
        {
            await session.OptimizedSync!.SynchronizeAsync(null!);
        }

        Assert.NotNull(askpass);
        Assert.StartsWith(configuredRoot + Path.DirectorySeparatorChar, askpass);
    }

    [Fact]
    public async Task WriteAskpass_RejectsAQuoteInThePath()
    {
        string directory = Path.Combine(_temporaryRoot, "it's");
        Directory.CreateDirectory(directory);

        var exception = await Assert.ThrowsAsync<ProtocolConfigurationException>(() =>
            ProtocolTemporaryFile.WriteAskpassAsync(directory, Password, CancellationToken.None));

        Assert.Equal("temp_path_invalid", exception.Code);
        Assert.Empty(Directory.GetFileSystemEntries(directory));
    }

    [Fact]
    public void DeleteOrphans_RemovesOnlyProtocolEntriesAndToleratesMissingRoots()
    {
        string orphanDirectory = Path.Combine(_temporaryRoot, ProtocolTemporaryFile.NamePrefix + "dir");
        Directory.CreateDirectory(orphanDirectory);
        File.WriteAllText(Path.Combine(orphanDirectory, "ssh-password"), Password);
        string orphanFile = Path.Combine(_temporaryRoot, ProtocolTemporaryFile.NamePrefix + "file");
        File.WriteAllText(orphanFile, "key");
        string unrelated = WriteFile("unrelated", "keep");

        ProtocolTemporaryFile.DeleteOrphans([_temporaryRoot, Path.Combine(_temporaryRoot, "missing"), null]);

        Assert.False(Directory.Exists(orphanDirectory));
        Assert.False(File.Exists(orphanFile));
        Assert.True(File.Exists(unrelated));
    }

    [Fact]
    public void TryDelete_DoesNotThrowWhenTheSecretIsBusy()
    {
        string directory = Path.Combine(_temporaryRoot, "busy");
        Directory.CreateDirectory(directory);
        using var held = new FileStream(
            Path.Combine(directory, "ssh-password"), FileMode.Create, FileAccess.Write, FileShare.None);

        ProtocolTemporaryFile.TryDelete(directory);
    }

    private static RsyncSshConnectionSettings ShellSettings() => new(
        "backup.example.test", 2222, "backup", "/srv/archive", "SHA256:unused",
        PrivateKeySecretReference: null, KnownHostsSecretReference: "/secrets/known_hosts");

    private StorageConnection PasswordConnection(string? privateKey = null)
    {
        byte[] hostKey = RandomNumberGenerator.GetBytes(32);
        string knownHosts = WriteFile(
            $"known_hosts_{Guid.NewGuid():N}",
            $"backup.example.test ssh-ed25519 {Convert.ToBase64String(hostKey)}");
        return new StorageConnection
        {
            ProviderId = "rsync-ssh",
            Name = "NAS",
            AuthorizationMode = StorageAuthorizationMode.UsernamePassword,
            EncryptedCredentialPayload = "protected",
            SettingsJson = JsonSerializer.Serialize(new RsyncSshConnectionSettings(
                "backup.example.test", 22, "backup", "/srv/archive",
                PinnedSshSettings.ComputeFingerprint(hostKey), privateKey, knownHosts))
        };
    }

    private string WriteFile(string name, string content)
    {
        string path = Path.Combine(_temporaryRoot, name);
        File.WriteAllText(path, content);
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        return path;
    }

    private static ICredentialVault Vault()
    {
        var vault = new Mock<ICredentialVault>();
        vault.Setup(candidate => candidate.Unprotect<Dictionary<string, string>>(
                It.IsAny<string>(), It.IsAny<CredentialContext>()))
            .Returns(new Dictionary<string, string> { ["username"] = "backup", ["password"] = Password });
        return vault.Object;
    }
}

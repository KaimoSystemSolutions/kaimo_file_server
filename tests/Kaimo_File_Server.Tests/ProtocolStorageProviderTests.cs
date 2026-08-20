using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Text.Json;
using Kaimo_File_Server.Core.Domain;
using Kaimo_File_Server.Core.Services;
using Kaimo_File_Server.Core.Services.ExternalStorage;
using Kaimo_File_Server.Infrastructure.ExternalStorage;
using Kaimo_File_Server.Web.Components.ViewModels;
using Moq;
using Xunit;

namespace Kaimo_File_Server.Tests;

public sealed class ProtocolStorageProviderTests : IDisposable
{
    private readonly string _temporaryRoot = Path.Combine(
        Path.GetTempPath(), $"kaimo-protocol-tests-{Guid.NewGuid():N}");

    public ProtocolStorageProviderTests() => Directory.CreateDirectory(_temporaryRoot);

    [Fact]
    public void Catalog_RejectsDuplicateProviderIdsIgnoringCase()
    {
        var first = new Mock<IStorageConnectionProvider>();
        first.SetupGet(provider => provider.Id).Returns("SMB");
        var second = new Mock<IStorageConnectionProvider>();
        second.SetupGet(provider => provider.Id).Returns("smb");

        Assert.Throws<ArgumentException>(() =>
            new StorageConnectionProviderCatalog([first.Object, second.Object]));
    }

    [Fact]
    public async Task DirectoryTargetResolver_ResolvesPathWithoutRequiringStableItemId()
    {
        var files = new Mock<IRemoteFileStore>();
        files.Setup(candidate => candidate.ListAsync("/Team/Documents", It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);
        files.Setup(candidate => candidate.ListAsync("/Team", It.IsAny<CancellationToken>()))
            .ReturnsAsync([new RemoteStorageItem("Documents", "/Team/Documents", true, null, null)]);
        var session = new Mock<IStorageSession>();
        session.SetupGet(candidate => candidate.RemoteFiles).Returns(files.Object);
        var provider = new Mock<IStorageConnectionProvider>();
        provider.SetupGet(candidate => candidate.Id).Returns("smb");
        provider.SetupGet(candidate => candidate.Capabilities)
            .Returns(StorageProviderCapabilities.Browse | StorageProviderCapabilities.Sync);
        provider.Setup(candidate => candidate.OpenSessionAsync(
                It.IsAny<StorageConnection>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(session.Object);
        var resolver = new StorageDirectoryTargetResolver(
            new StorageConnectionProviderCatalog([provider.Object]));

        var target = await resolver.ResolveDirectoryAsync(
            new StorageConnection { ProviderId = "smb" }, "//Team//Documents/");

        Assert.Equal("/Team/Documents", target.Path);
        Assert.Null(target.StableId);
        session.Verify(candidate => candidate.DisposeAsync(), Times.Once);
    }

    [Fact]
    public async Task DirectoryTargetResolver_RejectsTraversalBeforeOpeningProviderSession()
    {
        var provider = new Mock<IStorageConnectionProvider>();
        provider.SetupGet(candidate => candidate.Id).Returns("mock-storage");
        provider.SetupGet(candidate => candidate.Capabilities).Returns(StorageProviderCapabilities.Browse);
        var resolver = new StorageDirectoryTargetResolver(
            new StorageConnectionProviderCatalog([provider.Object]));

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => resolver.ResolveDirectoryAsync(
            new StorageConnection { ProviderId = "mock-storage" }, "/exports/../private"));

        provider.Verify(candidate => candidate.OpenSessionAsync(
            It.IsAny<StorageConnection>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task SmbProvider_ListsSharesThroughDirectAuthenticatedConnection()
    {
        IReadOnlyList<string>? invokedArguments = null;
        string? authenticationFile = null;
        var runner = new Mock<IProtocolCommandRunner>();
        runner.Setup(candidate => candidate.RunAsync(
                "smbclient", It.IsAny<IReadOnlyList<string>>(), It.IsAny<CancellationToken>(), true))
            .Callback((string _, IReadOnlyList<string> arguments, CancellationToken _, bool _) =>
            {
                invokedArguments = arguments.ToArray();
                authenticationFile = arguments[arguments.ToList().IndexOf("-A") + 1];
                Assert.Contains("username = alice", File.ReadAllText(authenticationFile));
            })
            .ReturnsAsync(new ProtocolCommandResult(0, "Disk|Team|Shared files\nDisk|IPC$|IPC\n"));
        var provider = new SmbStorageConnectionProvider(CredentialVault(), runner.Object);
        var connection = Connection(
            "smb", StorageAuthorizationMode.UsernamePassword,
            new SmbConnectionSettings("files.example.test"));
        connection.EncryptedCredentialPayload = "protected";

        await using var session = await provider.OpenSessionAsync(connection);
        var shares = await session.RemoteFiles!.ListAsync("/");

        Assert.True(session.Capabilities.HasFlag(StorageProviderCapabilities.DirectFileAccess));
        Assert.False(session.Capabilities.HasFlag(StorageProviderCapabilities.RequiresHostMount));
        Assert.Collection(shares, share =>
        {
            Assert.Equal("Team", share.Name);
            Assert.Equal("/Team", share.Path);
        });
        Assert.DoesNotContain(invokedArguments!, argument => argument.Contains("secret", StringComparison.Ordinal));
        Assert.False(File.Exists(authenticationFile));
    }

    [Fact]
    public async Task SmbProvider_ParsesClassicDirectoryListingsAndBrowsesNestedDirectories()
    {
        var commands = new List<string>();
        var runner = new Mock<IProtocolCommandRunner>();
        runner.Setup(candidate => candidate.RunAsync(
                "smbclient", It.IsAny<IReadOnlyList<string>>(), It.IsAny<CancellationToken>(), true))
            .Callback((string _, IReadOnlyList<string> arguments, CancellationToken _, bool _) =>
                commands.Add(Command(arguments)))
            .ReturnsAsync((string _, IReadOnlyList<string> arguments, CancellationToken _, bool _) =>
            {
                string command = Command(arguments);
                string output = command == "ls"
                    ? "  .                                   D        0  Wed Aug 19 10:00:00 2026\n"
                      + "  ..                                  D        0  Wed Aug 19 10:00:00 2026\n"
                      + "  Project files                       D        0  Wed Aug 19 10:00:00 2026\n"
                      + "  quarterly report.pdf                A     4711  Wed Aug 19 10:01:00 2026\n"
                      + "                123456 blocks of size 4096. 65432 blocks available\n"
                    : "  Nested folder                       D        0  Wed Aug 19 10:02:00 2026\n";
                return new ProtocolCommandResult(0, output);
            });
        var provider = SmbProvider(runner.Object);

        await using var session = await provider.OpenSessionAsync(SmbConnection());
        var root = await session.RemoteFiles!.ListAsync("/Team");
        var nested = await session.RemoteFiles.ListAsync("/Team/Project files");

        Assert.Collection(root,
            directory =>
            {
                Assert.Equal("Project files", directory.Name);
                Assert.Equal("/Team/Project files", directory.Path);
                Assert.True(directory.IsDirectory);
            },
            file =>
            {
                Assert.Equal("quarterly report.pdf", file.Name);
                Assert.Equal(4711, file.Size);
                Assert.False(file.IsDirectory);
                Assert.Equal(2026, file.ModifiedAtUtc?.Year);
            });
        Assert.Collection(nested, directory =>
        {
            Assert.Equal("Nested folder", directory.Name);
            Assert.Equal("/Team/Project files/Nested folder", directory.Path);
        });
        Assert.Equal(["ls", "cd \"Project files\"; ls"], commands);
    }

    [Theory]
    [InlineData("D|0|Wed Aug 19 10:00:00 2026|Documents")]
    [InlineData("Documents|D|0|Wed Aug 19 10:00:00 2026")]
    public async Task SmbProvider_AcceptsKnownPipeDirectoryFormats(string output)
    {
        var runner = RunnerReturning(output);
        await using var session = await SmbProvider(runner.Object).OpenSessionAsync(SmbConnection());

        var items = await session.RemoteFiles!.ListAsync("/Team");

        Assert.Collection(items, item =>
        {
            Assert.Equal("Documents", item.Name);
            Assert.True(item.IsDirectory);
        });
    }

    [Fact]
    public async Task SmbProvider_ExecutesCreateMoveAndDeleteOperationsWithShareRelativePaths()
    {
        var calls = new List<(string Command, bool ThrowOnFailure)>();
        var runner = new Mock<IProtocolCommandRunner>();
        runner.Setup(candidate => candidate.RunAsync(
                "smbclient", It.IsAny<IReadOnlyList<string>>(), It.IsAny<CancellationToken>(), It.IsAny<bool>()))
            .Callback((string _, IReadOnlyList<string> arguments, CancellationToken _, bool throwOnFailure) =>
                calls.Add((Command(arguments), throwOnFailure)))
            .ReturnsAsync((string _, IReadOnlyList<string> arguments, CancellationToken _, bool _) =>
                new ProtocolCommandResult(Command(arguments).StartsWith("del ", StringComparison.Ordinal) ? 1 : 0, ""));
        await using var session = await SmbProvider(runner.Object).OpenSessionAsync(SmbConnection());
        var files = session.RemoteFiles!;

        await files.CreateDirectoryAsync("/Team/Parent/New folder");
        await files.MoveAsync("/Team/Parent/old.txt", "/Team/Archive/new.txt");
        await files.DeleteAsync("/Team/Archive/new.txt", recursive: false);
        await files.DeleteAsync("/Team/Parent/New folder", recursive: true);

        Assert.Equal([
            ("mkdir \"Parent\\New folder\"", true),
            ("rename \"Parent\\old.txt\" \"Archive\\new.txt\"", true),
            ("del \"Archive\\new.txt\"", false),
            ("rmdir \"Archive\\new.txt\"", true),
            ("deltree \"Parent\\New folder\"", true)
        ], calls);
    }

    [Fact]
    public async Task SmbProvider_RoundTripsReadAndWriteStreamsThroughTemporaryFiles()
    {
        string? uploaded = null;
        var runner = new Mock<IProtocolCommandRunner>();
        runner.Setup(candidate => candidate.RunAsync(
                "smbclient", It.IsAny<IReadOnlyList<string>>(), It.IsAny<CancellationToken>(), true))
            .Callback((string _, IReadOnlyList<string> arguments, CancellationToken _, bool _) =>
            {
                string command = Command(arguments);
                string[] quoted = QuotedArguments(command);
                if (command.StartsWith("put ", StringComparison.Ordinal))
                    uploaded = File.ReadAllText(quoted[0]);
                else if (command.StartsWith("get ", StringComparison.Ordinal))
                    File.WriteAllText(quoted[1], "downloaded-content");
            })
            .ReturnsAsync(new ProtocolCommandResult(0, ""));
        await using var session = await SmbProvider(runner.Object).OpenSessionAsync(SmbConnection());
        var files = session.RemoteFiles!;

        await files.WriteAsync("/Team/Folder/upload.txt",
            new MemoryStream(Encoding.UTF8.GetBytes("uploaded-content")), overwrite: true);
        await using var downloaded = await files.OpenReadAsync("/Team/Folder/download.txt");
        using var reader = new StreamReader(downloaded, Encoding.UTF8);

        Assert.Equal("uploaded-content", uploaded);
        Assert.Equal("downloaded-content", await reader.ReadToEndAsync());
    }

    [Fact]
    public void SmbProvider_KeepsLocalPathsNativeAndNormalizesOnlyRemotePaths()
    {
        Assert.Equal("\"/tmp/kaimo upload.tmp\"",
            SmbCommandRemoteFileStore.QuoteLocal("/tmp/kaimo upload.tmp"));
        Assert.Equal("\"Folder\\file.txt\"",
            SmbCommandRemoteFileStore.QuoteRemote("Folder/file.txt"));
    }

    [Theory]
    [InlineData("NT_STATUS_ACCESS_DENIED opening remote file")]
    [InlineData("NT_STATUS_PRIVILEGE_NOT_HELD")]
    [InlineData("Permission denied")]
    public void ProtocolRunner_RecognizesRemotePermissionFailures(string output)
        => Assert.True(ProtocolCommandRunner.IsAccessDeniedOutput(output));

    [Fact]
    public async Task SmbProvider_PropagatesRemotePermissionFailureFromUpload()
    {
        var runner = new Mock<IProtocolCommandRunner>();
        runner.Setup(candidate => candidate.RunAsync(
                "smbclient", It.IsAny<IReadOnlyList<string>>(), It.IsAny<CancellationToken>(), true))
            .ThrowsAsync(new RemoteStorageAccessDeniedException("denied"));
        await using var session = await SmbProvider(runner.Object).OpenSessionAsync(SmbConnection());

        await Assert.ThrowsAsync<RemoteStorageAccessDeniedException>(() =>
            session.RemoteFiles!.WriteAsync(
                "/Team/upload.txt", new MemoryStream([1, 2, 3]), overwrite: true));
    }

    [Fact]
    public void VirtualShare_MapsRemotePermissionFailureToExplicitUserMessage()
    {
        string message = RemoteCloudAccessFileBrowserViewModel.WriteError(
            new RemoteStorageAccessDeniedException("denied"), "fallback");

        Assert.Equal(
            Core.Language.Resources.ResourceManager.GetString("Web_ExternalStorage_RemoteWriteDenied"),
            message);
        Assert.NotEqual(Core.Language.Resources.Web_Error_AccessDenied, message);
    }

    [Fact]
    public async Task SmbProvider_WriteWithoutOverwriteRejectsExistingRemoteFile()
    {
        var runner = RunnerReturning(
            "  existing.txt                         A       12  Wed Aug 19 10:00:00 2026\n");
        await using var session = await SmbProvider(runner.Object).OpenSessionAsync(SmbConnection());

        await Assert.ThrowsAsync<IOException>(() => session.RemoteFiles!.WriteAsync(
            "/Team/Folder/existing.txt", new MemoryStream([1, 2, 3]), overwrite: false));

        runner.Verify(candidate => candidate.RunAsync(
            "smbclient",
            It.Is<IReadOnlyList<string>>(arguments => Command(arguments).StartsWith("put ", StringComparison.Ordinal)),
            It.IsAny<CancellationToken>(), true), Times.Never);
    }

    [Fact]
    public async Task SmbProvider_RequiresStrongTransportSettings()
    {
        var connection = Connection(
            "smb", StorageAuthorizationMode.UsernamePassword,
            new SmbConnectionSettings("files.example.test", RequireEncryption: false));
        connection.EncryptedCredentialPayload = "protected";

        var result = await new SmbStorageConnectionProvider(
            CredentialVault(), Mock.Of<IProtocolCommandRunner>()).TestAsync(connection);

        Assert.Equal(StorageConnectionHealthState.InvalidConfiguration, result.State);
        Assert.Equal("transport_policy_unsafe", result.Code);
    }

    [Fact]
    public async Task RsyncSshProvider_RequiresPinnedKnownHostAndExposesNoBrowseContract()
    {
        string privateKey = WriteFile("id_ed25519", "private-key-placeholder");
        byte[] publicKey = RandomNumberGenerator.GetBytes(32);
        string knownHosts = WriteFile(
            "known_hosts",
            $"backup.example.test ssh-ed25519 {Convert.ToBase64String(publicKey)}");
        string fingerprint = "SHA256:" + Convert.ToBase64String(SHA256.HashData(publicKey)).TrimEnd('=');
        var runner = new Mock<IRsyncProcessRunner>();
        runner.Setup(candidate => candidate.TestSshAsync(
                It.IsAny<RsyncSshConnectionSettings>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);
        var provider = new RsyncSshStorageConnectionProvider(runner.Object);
        var connection = Connection(
            "rsync-ssh",
            StorageAuthorizationMode.SshKey,
            new RsyncSshConnectionSettings(
                "backup.example.test", 22, "backup", "/srv/archive", fingerprint,
                privateKey, knownHosts));

        await using var session = await provider.OpenSessionAsync(connection);
        var health = await provider.TestAsync(connection);

        Assert.True(health.IsHealthy);
        Assert.True(session.Capabilities.HasFlag(StorageProviderCapabilities.Sync));
        Assert.True(session.Capabilities.HasFlag(StorageProviderCapabilities.OptimizedSync));
        Assert.False(session.Capabilities.HasFlag(StorageProviderCapabilities.Browse));
        Assert.Null(session.RemoteFiles);
        Assert.NotNull(session.OptimizedSync);
    }

    [Fact]
    public async Task RsyncSshProvider_ReportsPinnedHostMismatchAsIdentityFailure()
    {
        string privateKey = WriteFile("mismatch-key", "private-key-placeholder");
        string knownHosts = WriteFile(
            "mismatch-known-hosts",
            $"backup.example.test ssh-ed25519 {Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))}");
        string differentFingerprint = "SHA256:"
                                      + Convert.ToBase64String(SHA256.HashData(RandomNumberGenerator.GetBytes(32)))
                                          .TrimEnd('=');
        var connection = Connection(
            "rsync-ssh",
            StorageAuthorizationMode.SshKey,
            new RsyncSshConnectionSettings(
                "backup.example.test", 22, "backup", "/srv/archive", differentFingerprint,
                privateKey, knownHosts));

        var result = await new RsyncSshStorageConnectionProvider(Mock.Of<IRsyncProcessRunner>())
            .TestAsync(connection);

        Assert.Equal(StorageConnectionHealthState.IdentityMismatch, result.State);
        Assert.Equal("host_key_mismatch", result.Code);
    }

    [Fact]
    public async Task RsyncSshProvider_MaterializesProtectedKeyOnlyForOperationLifetime()
    {
        byte[] privateKeyBytes = Encoding.UTF8.GetBytes("protected-private-key");
        byte[] publicKey = RandomNumberGenerator.GetBytes(32);
        string knownHosts = WriteFile(
            "managed-known-hosts",
            $"backup.example.test ssh-ed25519 {Convert.ToBase64String(publicKey)}");
        string fingerprint = "SHA256:"
                             + Convert.ToBase64String(SHA256.HashData(publicKey)).TrimEnd('=');
        var connection = Connection(
            "rsync-ssh",
            StorageAuthorizationMode.SshKey,
            new RsyncSshConnectionSettings(
                "backup.example.test", 22, "backup", "/srv/archive", fingerprint,
                null, knownHosts));
        connection.EncryptedCredentialPayload = "protected";
        var vault = new Mock<ICredentialVault>();
        vault.Setup(candidate => candidate.Unprotect<Dictionary<string, string>>(
                "protected", It.IsAny<CredentialContext>()))
            .Returns(new Dictionary<string, string>
            {
                ["privateKeyBase64"] = Convert.ToBase64String(privateKeyBytes)
            });
        string? materializedPath = null;
        var runner = new Mock<IRsyncProcessRunner>();
        runner.Setup(candidate => candidate.TestSshAsync(
                It.IsAny<RsyncSshConnectionSettings>(), It.IsAny<CancellationToken>()))
            .Returns<RsyncSshConnectionSettings, CancellationToken>((settings, _) =>
            {
                materializedPath = settings.PrivateKeySecretReference;
                Assert.NotNull(materializedPath);
                Assert.Equal(privateKeyBytes, File.ReadAllBytes(materializedPath));
                return Task.FromResult(true);
            });
        var provider = new RsyncSshStorageConnectionProvider(runner.Object, vault.Object);

        var health = await provider.TestAsync(connection);

        Assert.True(health.IsHealthy);
        Assert.NotNull(materializedPath);
        Assert.False(File.Exists(materializedPath));
    }

    [Fact]
    public async Task RsyncSshSetup_PersistsAndRevokesOnlyValidatedManagedKnownHost()
    {
        byte[] key = RandomNumberGenerator.GetBytes(32);
        string encoded = Convert.ToBase64String(key);
        string fingerprint = "SHA256:"
                             + Convert.ToBase64String(SHA256.HashData(key)).TrimEnd('=');
        var candidate = new RsyncSshHostKeyCandidate(
            "ssh-ed25519",
            fingerprint,
            $"backup.example.test ssh-ed25519 {encoded}");
        var service = new RsyncSshSetupService(_temporaryRoot);

        string path = await service.PersistKnownHostAsync(candidate);

        Assert.True(File.Exists(path));
        Assert.Equal(candidate.KnownHostsLine, (await File.ReadAllTextAsync(path)).Trim());
        await service.DeleteManagedKnownHostAsync(path);
        Assert.False(File.Exists(path));

        var tampered = candidate with { FingerprintSha256 = fingerprint + "x" };
        await Assert.ThrowsAsync<ProtocolConfigurationException>(
            () => service.PersistKnownHostAsync(tampered));
    }

    [Fact]
    public void SftpProvider_ExposesBrowsableReadWriteFileContract()
    {
        var provider = new SftpStorageConnectionProvider(CredentialVault());

        Assert.Equal("sftp", provider.Id);
        Assert.True(provider.Capabilities.HasFlag(StorageProviderCapabilities.Browse));
        Assert.True(provider.Capabilities.HasFlag(StorageProviderCapabilities.Read));
        Assert.True(provider.Capabilities.HasFlag(StorageProviderCapabilities.Write));
        Assert.True(provider.Capabilities.HasFlag(StorageProviderCapabilities.DirectFileAccess));
        Assert.False(provider.Capabilities.HasFlag(StorageProviderCapabilities.OptimizedSync));
        Assert.Equal([StorageAuthorizationMode.SshKey], provider.AuthorizationModes);
    }

    [Fact]
    public void SftpProvider_ParseValidatesFingerprintWithoutRequiringKnownHostsFile()
    {
        string fingerprint = SampleFingerprint();
        var connection = Connection(
            "sftp",
            StorageAuthorizationMode.SshKey,
            new RsyncSshConnectionSettings(
                "backup.example.test", 22, "backup", "/srv/archive", fingerprint,
                PrivateKeySecretReference: null, KnownHostsSecretReference: string.Empty));

        var settings = PinnedSshSettings.ParseAndValidate(connection, "sftp", requireKnownHosts: false);
        Assert.Equal("/srv/archive", settings.RemoteRoot);

        var tampered = Connection(
            "sftp",
            StorageAuthorizationMode.SshKey,
            new RsyncSshConnectionSettings(
                "backup.example.test", 22, "backup", "/srv/archive", "not-a-fingerprint",
                PrivateKeySecretReference: null, KnownHostsSecretReference: string.Empty));
        var exception = Assert.Throws<ProtocolConfigurationException>(
            () => PinnedSshSettings.ParseAndValidate(tampered, "sftp", requireKnownHosts: false));
        Assert.Equal("host_key_invalid", exception.Code);
    }

    [Fact]
    public async Task SftpProvider_MissingPrivateKeyIsInvalidConfiguration()
    {
        var connection = Connection(
            "sftp",
            StorageAuthorizationMode.SshKey,
            new RsyncSshConnectionSettings(
                "backup.example.test", 22, "backup", "/srv/archive", SampleFingerprint(),
                PrivateKeySecretReference: null, KnownHostsSecretReference: string.Empty));
        // No credential payload and no external key reference: resolution must
        // fail before any network access is attempted.
        connection.EncryptedCredentialPayload = null;

        var result = await new SftpStorageConnectionProvider(CredentialVault()).TestAsync(connection);

        Assert.Equal(StorageConnectionHealthState.InvalidConfiguration, result.State);
        Assert.Equal("private_key_missing", result.Code);
    }

    [Fact]
    public void SftpPaths_RejectTraversalAndResolveBeneathRemoteRoot()
    {
        Assert.Equal("/", SftpPaths.NormalizeStorePath(null));
        Assert.Equal("/foo/bar", SftpPaths.NormalizeStorePath("foo\\bar"));
        Assert.Throws<ProtocolConfigurationException>(() => SftpPaths.NormalizeStorePath("/foo/../etc"));
        Assert.Throws<ProtocolConfigurationException>(() => SftpPaths.NormalizeStorePath("/a\nb"));

        Assert.Equal("/srv/archive", SftpPaths.ToServerPath("/srv/archive/", "/"));
        Assert.Equal("/srv/archive/team/docs", SftpPaths.ToServerPath("/srv/archive", "/team/docs"));
        Assert.Equal("/team/docs", SftpPaths.CombineChild("/team", "docs"));
        Assert.Equal("/docs", SftpPaths.CombineChild("/", "docs"));
    }

    private static string SampleFingerprint()
        => "SHA256:" + Convert.ToBase64String(
            SHA256.HashData(RandomNumberGenerator.GetBytes(32))).TrimEnd('=');

    public void Dispose()
    {
        if (Directory.Exists(_temporaryRoot))
            Directory.Delete(_temporaryRoot, recursive: true);
    }

    private string CreateDirectory(string name)
    {
        string path = Path.Combine(_temporaryRoot, name);
        Directory.CreateDirectory(path);
        return path;
    }

    private string WriteFile(string name, string content)
    {
        string path = Path.Combine(_temporaryRoot, name);
        File.WriteAllText(path, content);
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        return path;
    }

    private static ICredentialVault CredentialVault()
    {
        var vault = new Mock<ICredentialVault>();
        vault.Setup(candidate => candidate.Unprotect<Dictionary<string, string>>(
                It.IsAny<string>(), It.IsAny<CredentialContext>()))
            .Returns(new Dictionary<string, string>
            {
                ["username"] = "alice",
                ["password"] = "secret"
            });
        return vault.Object;
    }

    private static SmbStorageConnectionProvider SmbProvider(IProtocolCommandRunner runner)
        => new(CredentialVault(), runner);

    private static StorageConnection SmbConnection()
    {
        var connection = Connection(
            "smb", StorageAuthorizationMode.UsernamePassword,
            new SmbConnectionSettings("files.example.test"));
        connection.EncryptedCredentialPayload = "protected";
        return connection;
    }

    private static Mock<IProtocolCommandRunner> RunnerReturning(string output)
    {
        var runner = new Mock<IProtocolCommandRunner>();
        runner.Setup(candidate => candidate.RunAsync(
                "smbclient", It.IsAny<IReadOnlyList<string>>(), It.IsAny<CancellationToken>(), It.IsAny<bool>()))
            .ReturnsAsync(new ProtocolCommandResult(0, output));
        return runner;
    }

    private static string Command(IReadOnlyList<string> arguments)
    {
        int index = arguments.ToList().IndexOf("-c");
        return index < 0 ? string.Empty : arguments[index + 1];
    }

    private static string[] QuotedArguments(string command)
        => Regex.Matches(command, "\\\"(?<value>[^\\\"]*)\\\"")
            .Select(match => match.Groups["value"].Value)
            .ToArray();

    private static StorageConnection Connection<T>(
        string providerId,
        StorageAuthorizationMode authorizationMode,
        T settings) => new()
    {
        ProviderId = providerId,
        AuthorizationMode = authorizationMode,
        SettingsJson = JsonSerializer.Serialize(settings),
        State = StorageConnectionState.Ready
    };
}

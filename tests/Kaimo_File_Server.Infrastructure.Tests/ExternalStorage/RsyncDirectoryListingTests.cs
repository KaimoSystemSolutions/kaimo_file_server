using Kaimo_File_Server.Core.Domain;
using Kaimo_File_Server.Core.Services.ExternalStorage;
using Kaimo_File_Server.Infrastructure.Clouds;
using Kaimo_File_Server.Infrastructure.ExternalStorage;
using Moq;
using Xunit;

namespace Kaimo_File_Server.Infrastructure.Tests.ExternalStorage;

public sealed class RsyncDirectoryListingTests
{
    private const string Prefix = "drwxr-xr-x          4,096 2026/10/01 12:34:56 ";

    [Theory]
    [InlineData(Prefix + "Backups", "Backups")]
    [InlineData(Prefix + "Meine Daten", "Meine Daten")]
    [InlineData(Prefix + "Übersicht", "Übersicht")]
    [InlineData(Prefix + " leading space", " leading space")]
    [InlineData("drwxrwsr-t  12 2026/10/01 12:34:56 sticky", "sticky")]
    [InlineData(Prefix + ".", null)]
    [InlineData(Prefix + "..", null)]
    [InlineData("-rw-r--r--            123 2026/10/01 12:34:56 file.txt", null)]
    [InlineData("lrwxrwxrwx             12 2026/10/01 12:34:56 link -> /etc", null)]
    [InlineData(Prefix + @"bad\#012name", null)]
    [InlineData(Prefix + "glob[1]", null)]
    [InlineData("", null)]
    [InlineData("receiving incremental file list", null)]
    public void ListedDirectoryName_ReturnsOnlyAddressableChildFolders(string line, string? expected)
        => Assert.Equal(expected, RsyncProcessRunner.ListedDirectoryName(line));

    [Fact]
    public void BuildListArguments_ListsOneLevelBelowTheConnectionRoot()
    {
        var arguments = RsyncProcessRunner.BuildListArguments(Settings("/srv/archive"), "/Meine Daten");

        Assert.Contains("--list-only", arguments);
        Assert.Contains("--protect-args", arguments);
        Assert.Contains("--8-bit-output", arguments);
        Assert.DoesNotContain("--recursive", arguments);
        Assert.Equal("--", arguments[^2]);
        Assert.Equal("backup@backup.example.test:/srv/archive/Meine Daten/", arguments[^1]);
    }

    [Fact]
    public void RsyncFailure_KeepsTheServerReasonForTheLog()
    {
        Exception failure = RsyncProcessRunner.RsyncFailure(12, "  rsync: connection unexpectedly closed\n");

        Assert.IsType<IOException>(failure);
        Assert.Contains("exit code 12", failure.Message);
        Assert.Contains("rsync: connection unexpectedly closed", failure.Message);
        Assert.Equal("connection_failed", CloudSyncExecutionService.ClassifyError(failure));
    }

    [Fact]
    public void RsyncFailure_ReportsARefusedSenderRoleAsItsOwnCode()
    {
        Exception failure = RsyncProcessRunner.RsyncFailure(
            1, "ERROR: module is write only\nrsync error: syntax or usage error (code 1) at main.c(879) [sender=3.1.2]\n");

        Assert.Equal("rsync_read_denied", Assert.IsType<ProtocolConfigurationException>(failure).Code);
        Assert.Contains("module is write only", failure.Message);
        Assert.Equal("rsync_read_denied", CloudSyncExecutionService.ClassifyError(failure));
    }

    [Theory]
    [InlineData("Backups/", "Backups")]
    [InlineData("Meine Daten/", "Meine Daten")]
    [InlineData("Übersicht/", "Übersicht")]
    [InlineData("file.txt", null)]
    [InlineData("./", null)]
    [InlineData("../", null)]
    [InlineData("glob[1]/", null)]
    [InlineData("/", null)]
    [InlineData("", null)]
    public void ShellListedDirectoryName_ReturnsOnlyAddressableChildFolders(string line, string? expected)
        => Assert.Equal(expected, RsyncProcessRunner.ShellListedDirectoryName(line));

    [Fact]
    public void BuildShellListArguments_QuotesThePathForTheRemoteShell()
    {
        var arguments = RsyncProcessRunner.BuildShellListArguments(Settings("/srv/archive"), "/it's $(reboot)");

        Assert.Contains("-p", arguments);
        Assert.Equal("backup@backup.example.test", arguments[^2]);
        Assert.Equal(@"ls -1Ap -- '/srv/archive/it'\''s $(reboot)/'", arguments[^1]);
    }

    [Fact]
    public void BuildShellListArguments_RejectsUnsafePaths()
        => Assert.Throws<ProtocolConfigurationException>(
            () => RsyncProcessRunner.BuildShellListArguments(Settings("/srv"), "/a/../b"));

    [Fact]
    public async Task Resolver_PassesOnFoldersThatCanOnlyBeListed()
    {
        var refused = new RemoteFolderNotSyncableException([Folder("share")], "rsync_read_denied");
        var optimized = new Mock<IOptimizedStorageSync>();
        optimized.Setup(sync => sync.ListDirectoriesAsync("/", It.IsAny<CancellationToken>())).ThrowsAsync(refused);
        var session = new Mock<IStorageSession>();
        session.SetupGet(candidate => candidate.OptimizedSync).Returns(optimized.Object);
        var resolver = Resolver(
            StorageProviderCapabilities.Sync | StorageProviderCapabilities.OptimizedSync, session);

        var thrown = await Assert.ThrowsAsync<RemoteFolderNotSyncableException>(() =>
            resolver.ListDirectoriesAsync(new StorageConnection { ProviderId = "mock" }, "/"));

        Assert.Equal("share", Assert.Single(thrown.Folders).Name);
        Assert.Equal("rsync_read_denied", thrown.Code);
    }

    [Fact]
    public void BuildListArguments_HandlesTheServerRoot()
        => Assert.Equal(
            "backup@backup.example.test:/",
            RsyncProcessRunner.BuildListArguments(Settings("/"), "/")[^1]);

    [Theory]
    [InlineData("/a/../b")]
    [InlineData("/a*")]
    public void BuildListArguments_RejectsUnsafePaths(string path)
        => Assert.Throws<ProtocolConfigurationException>(
            () => RsyncProcessRunner.BuildListArguments(Settings("/srv"), path));

    [Theory]
    [InlineData("/")]
    [InlineData("/srv/backups")]
    [InlineData("/volume1/Meine Daten/Übersicht")]
    [InlineData("/data/it's (2026) & more")]
    public void ValidateRemotePath_AcceptsRealWorldFolders(string path)
        => PinnedSshSettings.ValidateRemotePath(path);

    [Theory]
    [InlineData("")]
    [InlineData("relative/path")]
    [InlineData("/a/../b")]
    [InlineData("/a/./b")]
    [InlineData("/a*")]
    [InlineData("/a?")]
    [InlineData("/a[b")]
    [InlineData("/a\\b")]
    [InlineData("/a\nb")]
    [InlineData("/a\n")]
    [InlineData("/a\0b")]
    public void ValidateRemotePath_RejectsTraversalGlobsAndControlCharacters(string path)
        => Assert.Throws<ProtocolConfigurationException>(() => PinnedSshSettings.ValidateRemotePath(path));

    [Fact]
    public async Task Resolver_ListsFoldersOfSyncOnlyProvidersSorted()
    {
        var optimized = new Mock<IOptimizedStorageSync>();
        optimized.Setup(sync => sync.ListDirectoriesAsync("/a/b", It.IsAny<CancellationToken>()))
            .ReturnsAsync([Folder("zeta"), Folder("alpha")]);
        var session = new Mock<IStorageSession>();
        session.SetupGet(candidate => candidate.OptimizedSync).Returns(optimized.Object);
        var resolver = Resolver(
            StorageProviderCapabilities.Sync | StorageProviderCapabilities.OptimizedSync, session);

        var items = await resolver.ListDirectoriesAsync(new StorageConnection { ProviderId = "mock" }, "//a//b/");

        Assert.Equal(["alpha", "zeta"], items.Select(item => item.Name));
        session.Verify(candidate => candidate.DisposeAsync(), Times.Once);
    }

    [Fact]
    public async Task Resolver_RejectsProvidersThatCannotListFolders()
    {
        var session = new Mock<IStorageSession>();
        var resolver = Resolver(StorageProviderCapabilities.Sync, session);

        await Assert.ThrowsAsync<NotSupportedException>(() =>
            resolver.ListDirectoriesAsync(new StorageConnection { ProviderId = "mock" }, "/"));
    }

    [Fact]
    public async Task Resolver_DoesNotTreatSyncOnlyProvidersAsDurableRoots()
    {
        var session = new Mock<IStorageSession>();
        var resolver = Resolver(
            StorageProviderCapabilities.Sync | StorageProviderCapabilities.OptimizedSync, session);

        await Assert.ThrowsAsync<NotSupportedException>(() =>
            resolver.ResolveDirectoryAsync(new StorageConnection { ProviderId = "mock" }, "/"));
    }

    [Fact]
    public async Task ProviderSession_ListsThroughTheProcessRunner()
    {
        var runner = new Mock<IRsyncProcessRunner>();
        runner.Setup(candidate => candidate.ListDirectoriesAsync(
                It.IsAny<RsyncSshConnectionSettings>(), "/x", It.IsAny<CancellationToken>()))
            .ReturnsAsync([Folder("child")]);
        IOptimizedStorageSync sync = new RsyncSshOptimizedSync(Settings("/srv"), runner.Object);

        var items = await sync.ListDirectoriesAsync("/x");

        Assert.Equal("child", Assert.Single(items).Name);
    }

    private static RemoteStorageItem Folder(string name) => new(name, "/" + name, true, null, null);

    private static StorageDirectoryTargetResolver Resolver(
        StorageProviderCapabilities capabilities, Mock<IStorageSession> session)
    {
        var provider = new Mock<IStorageConnectionProvider>();
        provider.SetupGet(candidate => candidate.Id).Returns("mock");
        provider.SetupGet(candidate => candidate.Capabilities).Returns(capabilities);
        provider.Setup(candidate => candidate.OpenSessionAsync(
                It.IsAny<StorageConnection>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(session.Object);
        return new StorageDirectoryTargetResolver(new StorageConnectionProviderCatalog([provider.Object]));
    }

    private static RsyncSshConnectionSettings Settings(string root) => new(
        "backup.example.test", 22, "backup", root, "SHA256:unused",
        PrivateKeySecretReference: "/secrets/id_key", KnownHostsSecretReference: "/secrets/known_hosts");
}

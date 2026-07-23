using System.Text;
using Kaimo_File_Server.Core.Domain;
using Kaimo_File_Server.Core.Domain.Identity;
using Kaimo_File_Server.Core.Repositories;
using Kaimo_File_Server.Core.Security;
using Kaimo_File_Server.Core.Services.File;
using Kaimo_File_Server.SmbBridge.Grpc;
using Kaimo_File_Server.SmbBridge.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Kaimo_File_Server.Tests;

public sealed class SnapshotGrpcServiceAclTests : IDisposable
{
    private readonly Mock<IShareRepository> _shares = new();
    private readonly Mock<IAuthenticationLookup> _auth = new();
    private readonly Mock<IAclService> _acl = new();
    private readonly Mock<IFileVersionService> _versions = new();
    private readonly Mock<IFileServiceFactory> _factory = new();
    private readonly Mock<IFileService> _files = new();
    private readonly string _root;
    private readonly string _cacheRoot;
    private readonly ShareDefinition _share;
    private readonly UserContext _user;
    private readonly SnapshotGrpcService _sut;

    public SnapshotGrpcServiceAclTests()
    {
        _root = Path.Combine(
            Path.GetTempPath(), "kaimo-snapshot-acl-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
        string sharePath = Path.Combine(_root, "share");
        _cacheRoot = Path.Combine(_root, "internal-cache");
        Directory.CreateDirectory(sharePath);
        _share = new ShareDefinition("share", sharePath, isEnabled: true);
        var user = new User(Guid.NewGuid(), "Alice", "alice", "hash", "nt");
        _user = new UserContext(user, [], [], []);

        _auth.Setup(a => a.ResolveUserContextAsync("alice")).ReturnsAsync(_user);
        _shares.Setup(s => s.GetByNameAsync("share")).ReturnsAsync(_share);
        _factory.Setup(f => f.CreateForShare(_share.Id, _share.Path))
            .Returns(_files.Object);
        _versions.Setup(v => v.GetVersionsAsync(_share.Id, It.IsAny<string>()))
            .ReturnsAsync([]);

        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Snapshots:Cache:RootPath"] = _cacheRoot
            })
            .Build();
        _sut = new SnapshotGrpcService(
            _shares.Object, _auth.Object, _acl.Object, _versions.Object,
            _factory.Object, configuration,
            NullLogger<SnapshotGrpcService>.Instance);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }

    [Fact]
    public async Task EnumerateSnapshots_Folder_UsesDirectoryAwareFileService()
    {
        DateTime at = new(2026, 7, 20, 10, 11, 12, DateTimeKind.Utc);
        _files.Setup(f => f.GetFolderSnapshotTimestampsAsync("docs", _user))
            .ReturnsAsync([at]);

        var reply = await _sut.EnumerateSnapshots(
            new EnumerateSnapshotsRequest
            {
                Username = "alice", Share = "share", Path = "docs"
            }, null!);

        Assert.Equal(["@GMT-2026.07.20-10.11.12"], reply.GmtTokens);
        _acl.Verify(a => a.HasAccessAsync(
            It.IsAny<UserContext>(), It.IsAny<Guid>(), It.IsAny<string>(),
            false, FilePermission.ListReadData), Times.Never);
    }

    [Fact]
    public async Task ResolveFolder_FiltersAndRemovesPreviouslyMaterializedDeniedFile()
    {
        DateTime at = new(2026, 7, 20, 10, 11, 12, DateTimeKind.Utc);
        string token = "@GMT-2026.07.20-10.11.12";
        byte[] content = Encoding.UTF8.GetBytes("safe");
        FileVersion visible = Version("docs/visible.txt", at, content.Length);
        _files.Setup(f => f.GetFolderSnapshotAsync("docs", at, _user))
            .ReturnsAsync([visible]);
        _versions.Setup(v => v.GetVersionAtAsync(_share.Id, "docs", at))
            .ReturnsAsync((FileVersion?)null);
        _versions.Setup(v => v.ReadVersionAsync(_share.Id, visible.FilePath, at))
            .ReturnsAsync(() => new MemoryStream(content));

        string scope = _user.User.Id.ToString("N");
        string shareScope = _share.Id.ToString("N");
        string denied = Path.Combine(
            _cacheRoot, shareScope, token, scope, "docs", "denied.txt");
        Directory.CreateDirectory(Path.GetDirectoryName(denied)!);
        File.WriteAllText(denied, "secret");

        string otherScope = Guid.NewGuid().ToString("N");
        string otherUserFile = Path.Combine(
            _cacheRoot, shareScope, token, otherScope, "docs", "secret.txt");
        Directory.CreateDirectory(Path.GetDirectoryName(otherUserFile)!);
        File.WriteAllText(otherUserFile, "other-user");

        var reply = await ResolveAsync("docs", token);

        Assert.True(reply.Found);
        Assert.Equal($"{shareScope}/{token}/{scope}/docs", reply.CachePath);
        Assert.False(File.Exists(denied));
        Assert.True(File.Exists(Path.Combine(
            _cacheRoot, shareScope, token, scope, "docs", "visible.txt")));
        Assert.True(File.Exists(otherUserFile));
        Assert.False(Directory.Exists(Path.Combine(
            _share.Path, ".kaimo-snapshots")));
        _versions.Verify(v => v.GetFolderSnapshotAsync(
            It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<DateTime>()),
            Times.Never);
    }

    [Fact]
    public async Task ResolveFolder_AclRevoked_ReturnsNotFoundWithoutMaterialization()
    {
        DateTime at = new(2026, 7, 20, 10, 11, 12, DateTimeKind.Utc);
        _versions.Setup(v => v.GetVersionAtAsync(_share.Id, "restricted", at))
            .ReturnsAsync((FileVersion?)null);
        _files.Setup(f => f.GetFolderSnapshotAsync("restricted", at, _user))
            .ThrowsAsync(new UnauthorizedAccessException());

        var reply = await ResolveAsync(
            "restricted", "@GMT-2026.07.20-10.11.12");

        Assert.False(reply.Found);
        _versions.Verify(v => v.ReadVersionAsync(
            It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<DateTime>()),
            Times.Never);
    }

    [Fact]
    public async Task ResolveConcreteFile_ReadDenied_DoesNotReuseOrCreateCache()
    {
        DateTime at = new(2026, 7, 20, 10, 11, 12, DateTimeKind.Utc);
        FileVersion version = Version("docs/secret.txt", at, 6);
        _versions.Setup(v => v.GetVersionAtAsync(_share.Id, version.FilePath, at))
            .ReturnsAsync(version);
        _acl.Setup(a => a.HasAccessAsync(
                _user, _share.Id, version.FilePath, false,
                FilePermission.ListReadData))
            .ReturnsAsync(false);

        var reply = await ResolveAsync(
            version.FilePath, "@GMT-2026.07.20-10.11.12");

        Assert.False(reply.Found);
        _versions.Verify(v => v.ReadVersionAsync(
            It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<DateTime>()),
            Times.Never);
    }

    [Fact]
    public async Task ResolveVersion_TraversalPath_IsRejectedBeforeVersionLookup()
    {
        var reply = await ResolveAsync(
            "../secret.txt", "@GMT-2026.07.20-10.11.12");

        Assert.False(reply.Found);
        _versions.Verify(v => v.GetVersionAtAsync(
            It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<DateTime>()),
            Times.Never);
        _factory.Verify(f => f.CreateForShare(
            It.IsAny<Guid>(), It.IsAny<string>()), Times.Never);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ResolveVersion_CacheOverlappingShare_FailsClosed(
        bool cacheContainsShare)
    {
        DateTime at = new(2026, 7, 20, 10, 11, 12, DateTimeKind.Utc);
        FileVersion version = Version("docs/file.txt", at, 4);
        _versions.Setup(v => v.GetVersionAtAsync(_share.Id, version.FilePath, at))
            .ReturnsAsync(version);

        var unsafeConfiguration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Snapshots:Cache:RootPath"] = cacheContainsShare
                    ? _root
                    : Path.Combine(_share.Path, ".kaimo-snapshots")
            })
            .Build();
        var unsafeService = new SnapshotGrpcService(
            _shares.Object, _auth.Object, _acl.Object, _versions.Object,
            _factory.Object, unsafeConfiguration,
            NullLogger<SnapshotGrpcService>.Instance);

        var reply = await unsafeService.ResolveVersion(
            new ResolveVersionRequest
            {
                Username = "alice",
                Share = "share",
                Path = version.FilePath,
                GmtToken = "@GMT-2026.07.20-10.11.12"
            }, null!);

        Assert.False(reply.Found);
        _versions.Verify(v => v.GetVersionAtAsync(
            It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<DateTime>()),
            Times.Never);
        _versions.Verify(v => v.ReadVersionAsync(
            It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<DateTime>()),
            Times.Never);
    }

    private Task<ResolveVersionReply> ResolveAsync(string path, string token) =>
        _sut.ResolveVersion(
            new ResolveVersionRequest
            {
                Username = "alice",
                Share = "share",
                Path = path,
                GmtToken = token
            }, null!);

    private FileVersion Version(string path, DateTime at, int size) =>
        new(_share.Id, path, at, "blob", "hash", size, null, 1);
}

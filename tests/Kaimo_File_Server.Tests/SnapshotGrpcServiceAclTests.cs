using System.Security.Cryptography;
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
            _factory.Object, new SnapshotCacheLeaseManager(),
            new SnapshotMaterializationLimiter(configuration), configuration,
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
        FileVersion visible = Version("docs/visible.txt", at, content);
        _files.Setup(f => f.GetFolderSnapshotAsync(
                "docs", at, _user, It.IsAny<CancellationToken>()))
            .ReturnsAsync([visible]);
        _versions.Setup(v => v.GetVersionAtAsync(_share.Id, "docs", at))
            .ReturnsAsync((FileVersion?)null);
        _versions.Setup(v => v.ReadVersionAsync(
                _share.Id, visible.FilePath, at,
                It.IsAny<CancellationToken>()))
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
        _files.Setup(f => f.GetFolderSnapshotAsync(
                "restricted", at, _user, It.IsAny<CancellationToken>()))
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
    [InlineData("../escaped.txt")]
    [InlineData("/escaped.txt")]
    [InlineData(".kaimo-close-captures/event.cap")]
    public async Task ResolveVersion_InvalidPersistedPath_IsNotMaterialized(
        string persistedPath)
    {
        DateTime at = new(2026, 7, 20, 10, 11, 12, DateTimeKind.Utc);
        const string requestPath = "docs/file.txt";
        FileVersion version = Version(persistedPath, at, 4);
        _versions.Setup(v => v.GetVersionAtAsync(
                _share.Id, requestPath, at))
            .ReturnsAsync(version);
        _acl.Setup(a => a.HasAccessAsync(
                _user, _share.Id, requestPath, false,
                FilePermission.ListReadData))
            .ReturnsAsync(true);

        var reply = await ResolveAsync(
            requestPath, "@GMT-2026.07.20-10.11.12");

        Assert.False(reply.Found);
        _versions.Verify(v => v.ReadVersionAsync(
            It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<DateTime>(),
            It.IsAny<CancellationToken>()), Times.Never);
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
            _factory.Object, new SnapshotCacheLeaseManager(),
            new SnapshotMaterializationLimiter(unsafeConfiguration),
            unsafeConfiguration,
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

    [Fact]
    public async Task ResolveConcreteFile_ReplacesSameSizedCorruptCacheAtomically()
    {
        DateTime at = new(2026, 7, 20, 10, 11, 12, DateTimeKind.Utc);
        string token = "@GMT-2026.07.20-10.11.12";
        byte[] expected = Encoding.UTF8.GetBytes("good");
        FileVersion version = Version("docs/file.txt", at, expected);
        AllowConcreteFile(version, expected);

        string final = CachePath(token, version.FilePath);
        Directory.CreateDirectory(Path.GetDirectoryName(final)!);
        await File.WriteAllBytesAsync(final, Encoding.UTF8.GetBytes("evil"));

        var reply = await ResolveAsync(version.FilePath, token);

        Assert.True(reply.Found);
        Assert.Equal(expected, await File.ReadAllBytesAsync(final));
        Assert.Empty(Directory.EnumerateFiles(
            Path.GetDirectoryName(final)!, "*.kaimo-tmp-*"));
    }

    [Fact]
    public async Task ResolveConcreteFile_HashMismatchDoesNotPublishPartialFile()
    {
        DateTime at = new(2026, 7, 20, 10, 11, 12, DateTimeKind.Utc);
        string token = "@GMT-2026.07.20-10.11.12";
        byte[] expected = Encoding.UTF8.GetBytes("good");
        byte[] corrupt = Encoding.UTF8.GetBytes("evil");
        FileVersion version = Version("docs/file.txt", at, expected);
        AllowConcreteFile(version, corrupt);

        var reply = await ResolveAsync(version.FilePath, token);

        string final = CachePath(token, version.FilePath);
        Assert.False(reply.Found);
        Assert.False(File.Exists(final));
        Assert.Empty(Directory.EnumerateFiles(
            Path.GetDirectoryName(final)!, "*.kaimo-tmp-*"));
    }

    [Theory]
    [InlineData("short")]
    [InlineData("content-too-long")]
    public async Task ResolveConcreteFile_LengthMismatchDoesNotPublishPartialFile(
        string actualText)
    {
        DateTime at = new(2026, 7, 20, 10, 11, 12, DateTimeKind.Utc);
        string token = "@GMT-2026.07.20-10.11.12";
        byte[] expected = Encoding.UTF8.GetBytes("expected");
        byte[] actual = Encoding.UTF8.GetBytes(actualText);
        FileVersion version = Version("docs/file.txt", at, expected);
        AllowConcreteFile(version, actual);

        var reply = await ResolveAsync(version.FilePath, token);

        string final = CachePath(token, version.FilePath);
        Assert.False(reply.Found);
        Assert.False(File.Exists(final));
        Assert.Empty(Directory.EnumerateFiles(
            Path.GetDirectoryName(final)!, "*.kaimo-tmp-*"));
    }

    [Fact]
    public async Task ResolveFolder_FileQuotaExceeded_FailsBeforeMaterialization()
    {
        DateTime at = new(2026, 7, 20, 10, 11, 12, DateTimeKind.Utc);
        string token = "@GMT-2026.07.20-10.11.12";
        FileVersion first = Version("docs/one.txt", at, [1]);
        FileVersion second = Version("docs/two.txt", at, [2]);
        SetupFolder("docs", at, first, second);
        SnapshotGrpcService sut = CreateService(
            ("Snapshots:Materialization:MaxFilesPerRequest", "1"));

        ResolveVersionReply reply = await ResolveAsync(sut, "docs", token);

        Assert.False(reply.Found);
        Assert.False(Directory.Exists(Path.Combine(
            _cacheRoot, _share.Id.ToString("N"), token,
            _user.User.Id.ToString("N"), "docs")));
        _versions.Verify(v => v.ReadVersionAsync(
            It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<DateTime>(),
            It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task ResolveFolder_ByteQuotaExceeded_FailsBeforeMaterialization()
    {
        DateTime at = new(2026, 7, 20, 10, 11, 12, DateTimeKind.Utc);
        string token = "@GMT-2026.07.20-10.11.12";
        FileVersion version = Version("docs/file.txt", at, [1, 2, 3, 4]);
        SetupFolder("docs", at, version);
        SnapshotGrpcService sut = CreateService(
            ("Snapshots:Materialization:MaxBytesPerRequest", "3"));

        ResolveVersionReply reply = await ResolveAsync(sut, "docs", token);

        Assert.False(reply.Found);
        _versions.Verify(v => v.ReadVersionAsync(
            It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<DateTime>(),
            It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task ResolveFolder_Timeout_RemovesFilesCreatedByAbandonedRequest()
    {
        DateTime at = new(2026, 7, 20, 10, 11, 12, DateTimeKind.Utc);
        string token = "@GMT-2026.07.20-10.11.12";
        byte[] firstContent = Encoding.UTF8.GetBytes("first");
        FileVersion first = Version("docs/one.txt", at, firstContent);
        FileVersion blocked = Version("docs/two.txt", at, [2]);
        SetupFolder("docs", at, first, blocked);
        _versions.Setup(v => v.ReadVersionAsync(
                _share.Id, first.FilePath, at,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => new MemoryStream(firstContent));
        _versions.Setup(v => v.ReadVersionAsync(
                _share.Id, blocked.FilePath, at,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => new CancellationBlockingStream());
        SnapshotGrpcService sut = CreateService(
            ("Snapshots:Materialization:MaxDurationSeconds", "1"));

        ResolveVersionReply reply = await ResolveAsync(sut, "docs", token);

        Assert.False(reply.Found);
        Assert.False(File.Exists(CachePath(token, first.FilePath)));
        string tokenRoot = Path.Combine(
            _cacheRoot, _share.Id.ToString("N"), token);
        Assert.Empty(Directory.Exists(tokenRoot)
            ? Directory.EnumerateFiles(
                tokenRoot, "*.kaimo-tmp-*", SearchOption.AllDirectories)
            : []);
    }

    [Fact]
    public async Task MaterializationLimiter_ConcurrencySlotHonorsCancellation()
    {
        IConfiguration configuration = Configuration(
            ("Snapshots:Materialization:MaxConcurrentRequests", "1"));
        var limiter = new SnapshotMaterializationLimiter(configuration);
        FileVersion version = Version(
            "docs/file.txt",
            new DateTime(2026, 7, 20, 10, 11, 12, DateTimeKind.Utc),
            [1]);
        using SnapshotMaterializationLimiter.Reservation first =
            await limiter.ReserveAsync([version], CancellationToken.None);
        using var cancellation = new CancellationTokenSource(
            TimeSpan.FromMilliseconds(100));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await limiter.ReserveAsync([version], cancellation.Token));
    }

    private async Task<ResolveVersionReply> ResolveAsync(
        string path, string token)
        => await ResolveAsync(_sut, path, token);

    private static async Task<ResolveVersionReply> ResolveAsync(
        SnapshotGrpcService sut, string path, string token)
    {
        ResolveVersionReply reply = await sut.ResolveVersion(
            new ResolveVersionRequest
            {
                Username = "alice",
                Share = "share",
                Path = path,
                GmtToken = token
            }, null!);
        if (!string.IsNullOrEmpty(reply.LeaseId))
            await sut.ReleaseVersionLease(
                new ReleaseVersionLeaseRequest { LeaseId = reply.LeaseId },
                null!);
        return reply;
    }

    private SnapshotGrpcService CreateService(
        params (string Key, string Value)[] settings)
    {
        IConfiguration configuration = Configuration(settings);
        return new SnapshotGrpcService(
            _shares.Object, _auth.Object, _acl.Object, _versions.Object,
            _factory.Object, new SnapshotCacheLeaseManager(),
            new SnapshotMaterializationLimiter(configuration), configuration,
            NullLogger<SnapshotGrpcService>.Instance);
    }

    private IConfiguration Configuration(
        params (string Key, string Value)[] settings)
    {
        var values = new Dictionary<string, string?>
        {
            ["Snapshots:Cache:RootPath"] = _cacheRoot
        };
        foreach ((string key, string value) in settings)
            values[key] = value;
        return new ConfigurationBuilder()
            .AddInMemoryCollection(values)
            .Build();
    }

    private void SetupFolder(
        string path, DateTime at, params FileVersion[] versions)
    {
        _versions.Setup(v => v.GetVersionAtAsync(_share.Id, path, at))
            .ReturnsAsync((FileVersion?)null);
        _files.Setup(f => f.GetFolderSnapshotAsync(
                path, at, _user, It.IsAny<CancellationToken>()))
            .ReturnsAsync(versions.ToList());
    }

    private void AllowConcreteFile(FileVersion version, byte[] content)
    {
        _versions.Setup(v => v.GetVersionAtAsync(
                _share.Id, version.FilePath, version.SnapshotTimestampUtc))
            .ReturnsAsync(version);
        _acl.Setup(a => a.HasAccessAsync(
                _user, _share.Id, version.FilePath, false,
                FilePermission.ListReadData))
            .ReturnsAsync(true);
        _versions.Setup(v => v.ReadVersionAsync(
                _share.Id, version.FilePath, version.SnapshotTimestampUtc,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => new MemoryStream(content));
    }

    private string CachePath(string token, string relativePath) =>
        Path.Combine(
            _cacheRoot,
            _share.Id.ToString("N"),
            token,
            _user.User.Id.ToString("N"),
            relativePath.Replace('/', Path.DirectorySeparatorChar));

    private FileVersion Version(string path, DateTime at, byte[] content) =>
        new(
            _share.Id, path, at, "blob",
            Convert.ToHexString(SHA256.HashData(content)),
            content.LongLength, null, 1);

    private FileVersion Version(string path, DateTime at, int size) =>
        new(_share.Id, path, at, "blob", new string('0', 64), size, null, 1);

    private sealed class CancellationBlockingStream : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) =>
            throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) =>
            throw new NotSupportedException();
        public override void SetLength(long value) =>
            throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) =>
            throw new NotSupportedException();

        public override async ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return 0;
        }
    }
}

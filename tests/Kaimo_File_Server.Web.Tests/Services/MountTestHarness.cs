using Kaimo_File_Server.Core.Domain;
using Kaimo_File_Server.Core.Domain.Identity;
using Kaimo_File_Server.Core.Helpers;
using Kaimo_File_Server.Core.Repositories;
using Kaimo_File_Server.Core.Security;
using Kaimo_File_Server.Core.Services;
using Kaimo_File_Server.Core.Services.ExternalStorage;
using Kaimo_File_Server.Core.Services.File;
using Kaimo_File_Server.Infrastructure.Storage;
using Kaimo_File_Server.Web.Services;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.Extensions.Caching.Memory;
using Moq;

namespace Kaimo_File_Server.Web.Tests.Services;

/// <summary>
/// In-memory provider store that reports a missing folder the way the WebDAV store does: as a
/// plain <see cref="IOException"/>, indistinguishable from any other storage failure.
/// </summary>
internal sealed class FakeRemoteFileStore : IRemoteFileStore
{
    public Dictionary<string, byte[]> Files { get; } = new(StringComparer.Ordinal);
    public HashSet<string> Directories { get; } = new(StringComparer.Ordinal) { "root" };
    public Dictionary<string, int> ListCalls { get; } = new(StringComparer.Ordinal);
    public List<(string Path, DateTime Modified, DateTime? Created, bool IsDirectory)> Stamped { get; } = [];
    public DateTime ModifiedAt { get; set; } = new(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc);
    public Func<string, Exception?> ListFailure { get; set; } = _ => null;
    public Exception? WriteFailure { get; set; }
    public Exception? StampFailure { get; set; }
    public bool SeekableReads { get; set; } = true;
    public int ReadCalls { get; private set; }

    public Task<IReadOnlyList<RemoteStorageItem>> ListAsync(string path, CancellationToken cancellationToken = default)
    {
        ListCalls[path] = ListCalls.GetValueOrDefault(path) + 1;
        if (ListFailure(path) is { } failure) throw failure;
        if (!Directories.Contains(path)) throw new IOException($"Not found: {path}");

        var items = new List<RemoteStorageItem>();
        items.AddRange(Directories.Where(d => d != path && ShareRelativePath.GetParent(d) == path)
            .Select(d => new RemoteStorageItem(ShareRelativePath.GetFileName(d), d, true, null, ModifiedAt)));
        items.AddRange(Files.Where(f => ShareRelativePath.GetParent(f.Key) == path)
            .Select(f => new RemoteStorageItem(ShareRelativePath.GetFileName(f.Key), f.Key, false, f.Value.Length, ModifiedAt)));
        return Task.FromResult<IReadOnlyList<RemoteStorageItem>>(items);
    }

    public Task<Stream> OpenReadAsync(string path, CancellationToken cancellationToken = default)
    {
        ReadCalls++;
        if (!Files.TryGetValue(path, out var content)) throw new IOException($"Not found: {path}");
        Stream stream = new MemoryStream(content);
        return Task.FromResult(SeekableReads ? stream : new NonSeekableStream(stream));
    }

    public async Task WriteAsync(string path, Stream content, bool overwrite, CancellationToken cancellationToken = default)
    {
        if (WriteFailure is not null) throw WriteFailure;
        // Like SFTP's FileMode.Create: the old content is gone before the first byte arrives.
        Files[path] = [];
        using var buffer = new MemoryStream();
        await content.CopyToAsync(buffer, cancellationToken);
        Files[path] = buffer.ToArray();
    }

    public Task CreateDirectoryAsync(string path, CancellationToken cancellationToken = default)
    {
        Directories.Add(path);
        return Task.CompletedTask;
    }

    public Task DeleteAsync(string path, bool recursive, CancellationToken cancellationToken = default)
    {
        foreach (var file in Files.Keys.Where(f => f == path || f.StartsWith(path + "/", StringComparison.Ordinal)).ToList())
            Files.Remove(file);
        Directories.RemoveWhere(d => d == path || d.StartsWith(path + "/", StringComparison.Ordinal));
        return Task.CompletedTask;
    }

    public Task MoveAsync(string sourcePath, string destinationPath, CancellationToken cancellationToken = default)
    {
        if (Files.Remove(sourcePath, out var content)) Files[destinationPath] = content;
        else if (Directories.Remove(sourcePath)) Directories.Add(destinationPath);
        return Task.CompletedTask;
    }

    public Task TrySetTimesAsync(string path, DateTime modifiedUtc, DateTime? createdUtc, CancellationToken cancellationToken = default)
    {
        if (StampFailure is not null) throw StampFailure;
        Stamped.Add((path, modifiedUtc, createdUtc, false));
        return Task.CompletedTask;
    }

    public Task TrySetDirectoryTimesAsync(string path, DateTime modifiedUtc, DateTime? createdUtc, CancellationToken cancellationToken = default)
    {
        if (StampFailure is not null) throw StampFailure;
        Stamped.Add((path, modifiedUtc, createdUtc, true));
        return Task.CompletedTask;
    }

    private sealed class NonSeekableStream(Stream inner) : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => inner.Read(buffer, offset, count);
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}

/// <summary>
/// A local host share on disk with one virtual share mounted at Team/Cloud, and one standalone
/// virtual share. Both use the same provider folder.
/// </summary>
internal sealed class MountTestHarness : IDisposable
{
    public const StorageProviderCapabilities AllCapabilities =
        StorageProviderCapabilities.Browse | StorageProviderCapabilities.Read | StorageProviderCapabilities.Write
        | StorageProviderCapabilities.CreateDirectory | StorageProviderCapabilities.Delete
        | StorageProviderCapabilities.Rename | StorageProviderCapabilities.Move;

    private readonly MemoryCache _memory = new(new MemoryCacheOptions());
    private readonly string _root = Directory.CreateTempSubdirectory("kaimo-mount-").FullName;
    private readonly StorageConnection _connection = new() { ProviderId = "test", State = StorageConnectionState.Ready };

    public FakeRemoteFileStore Remote { get; } = new();
    public UserContext User { get; } = new(new User(Guid.NewGuid(), "A", "alice", "h", "nt"), [], [], []);
    public ShareDefinition Host { get; }
    public string HostPath => _root;
    public CloudAccessShare Share { get; }
    public CloudAccessShare Standalone { get; }
    public CloudAccessDirectoryCache Cache { get; }
    public Mock<ICloudAccessRepository> Repository { get; } = new();
    public Mock<IStorageConnectionRepository> Connections { get; } = new();
    public Mock<IAclService> Acl { get; } = new();
    public Mock<ICloudAccessMountGuard> MountGuard { get; } = new();
    public Mock<IFileServiceFactory> Factory { get; } = new();
    public StorageProviderCapabilities Capabilities { get; set; } = AllCapabilities;
    public CloudAccessPermission? Granted { get; set; } = CloudAccessPermission.Write;
    public int SessionsOpened { get; private set; }
    public int SessionsDisposed { get; private set; }
    public StorageConnection Connection => _connection;

    public MountTestHarness()
    {
        Host = new ShareDefinition("projekte", _root, null, true, false, false);
        Directory.CreateDirectory(Path.Combine(_root, "Team"));
        Share = new CloudAccessShare
        {
            ConnectionId = _connection.Id,
            Name = "Cloud",
            RemoteRootPath = "root",
            Provisioning = CloudAccessProvisioning.Folder,
            MountShareId = Host.Id,
            MountPath = "Team/Cloud"
        };
        Standalone = new CloudAccessShare { ConnectionId = _connection.Id, Name = "Archiv", RemoteRootPath = "root" };

        var settings = new Mock<ICloudAccessSettingsStore>();
        settings.Setup(x => x.GetAsync()).ReturnsAsync(CloudAccessRuntimeSettings.Default());
        Cache = new CloudAccessDirectoryCache(_memory, settings.Object);

        Repository.Setup(r => r.GetSharesAsync(It.IsAny<CancellationToken>())).ReturnsAsync(() => [Share, Standalone]);
        Repository.Setup(r => r.GetMountedSharesAsync(Host.Id, It.IsAny<CancellationToken>())).ReturnsAsync(() => [Share]);
        Repository.Setup(r => r.GetShareAsync(Share.Id, It.IsAny<CancellationToken>())).ReturnsAsync(Share);
        Repository.Setup(r => r.GetShareAsync(Standalone.Id, It.IsAny<CancellationToken>())).ReturnsAsync(Standalone);
        Connections.Setup(x => x.GetAsync(_connection.Id, It.IsAny<CancellationToken>())).ReturnsAsync(_connection);
        Connections.Setup(x => x.GetAllAsync(It.IsAny<CancellationToken>())).ReturnsAsync(() => [_connection]);
        Repository.Setup(r => r.GetEffectivePermissionAsync(It.IsAny<Guid>(), It.IsAny<IEnumerable<Guid>>()))
            .ReturnsAsync(() => Granted);

        Acl.Setup(a => a.HasAccessAsync(It.IsAny<UserContext>(), It.IsAny<Guid>(), It.IsAny<string>(),
                It.IsAny<bool>(), It.IsAny<FilePermission>()))
            .ReturnsAsync(true);
        Acl.Setup(a => a.HasAccessBatchAsync(It.IsAny<UserContext>(), It.IsAny<Guid>(),
                It.IsAny<IReadOnlyList<(string, bool)>>(), It.IsAny<FilePermission>()))
            .ReturnsAsync((UserContext _, Guid _, IReadOnlyList<(string Path, bool _)> items, FilePermission _) =>
                items.ToDictionary(i => i.Path, _ => true));
        MountGuard.Setup(g => g.IsReservedAsync(Host.Id, It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((Guid _, string path, CancellationToken _) =>
                ShareRelativePath.IsSameOrDescendant("Team/Cloud", path));

        Factory.Setup(f => f.CreateForShare(It.IsAny<ShareDefinition>()))
            .Returns<ShareDefinition>(s => new FileService(
                new FileSystemStorage(s.Path, s.Id, null), Acl.Object, null, s.Id, mountGuard: MountGuard.Object));
    }

    public CloudAccessRemoteSessionOpener NewOpener()
    {
        var provider = new Mock<IStorageConnectionProvider>();
        provider.Setup(x => x.OpenSessionAsync(_connection, It.IsAny<CancellationToken>()))
            .ReturnsAsync(() =>
            {
                SessionsOpened++;
                var session = new Mock<IStorageSession>();
                session.SetupGet(x => x.RemoteFiles).Returns(Remote);
                session.SetupGet(x => x.Capabilities).Returns(Capabilities);
                session.Setup(x => x.DisposeAsync()).Returns(() =>
                {
                    SessionsDisposed++;
                    return ValueTask.CompletedTask;
                });
                return session.Object;
            });
        var providers = new Mock<IStorageConnectionProviderCatalog>();
        providers.Setup(x => x.GetRequired("test")).Returns(provider.Object);
        return new CloudAccessRemoteSessionOpener(Repository.Object, Connections.Object, providers.Object);
    }

    private Mock<IShareRepository> NewShares()
    {
        var shares = new Mock<IShareRepository>();
        shares.Setup(s => s.GetByIdAsync(Host.Id)).ReturnsAsync(Host);
        return shares;
    }

    private CloudAccessAuthorizationService NewAuthorization(IShareRepository shares)
    {
        var management = new Mock<IManagementAuthService>();
        management.Setup(m => m.GetAuthorizedDepartmentIdsAsync(It.IsAny<UserContext>(), It.IsAny<ManagementPermission>()))
            .ReturnsAsync(AuthorizedScopeResult.None());
        return new CloudAccessAuthorizationService(Repository.Object, management.Object, shares, Acl.Object);
    }

    public CloudAccessMountResolver NewResolver()
    {
        var shares = NewShares();
        return new CloudAccessMountResolver(Repository.Object, shares.Object, NewAuthorization(shares.Object),
            new Mock<AuthenticationStateProvider>().Object, new Mock<IUserContextFactory>().Object);
    }

    public MountAwareFileServiceRouter NewRouter()
        => new(NewResolver(), Factory.Object, NewOpener(), Cache,
            Repository.Object, NewAuthorization(NewShares().Object), Connections.Object);

    public RemoteMountFileService NewAdapter(
        CloudAccessPermission permission = CloudAccessPermission.Write, bool freshListings = false)
        => new(new MountedFolder(Share.Id, Host.Name, "Team/Cloud") { Permission = permission }, NewOpener(), Cache, freshListings);

    public RemoteMountFileService NewStandaloneAdapter(CloudAccessPermission permission = CloudAccessPermission.Write)
        => new(new MountedFolder(Standalone.Id, Standalone.Name, string.Empty) { Permission = permission }, NewOpener(), Cache);

    public void Dispose()
    {
        _memory.Dispose();
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
    }
}

/// <summary>A router for controller tests that do not involve mounts: nothing is ever mounted.</summary>
internal static class TestMountRouters
{
    public static MountAwareFileServiceRouter None(IFileServiceFactory factory)
    {
        var repository = new Mock<ICloudAccessRepository>();
        repository.Setup(r => r.GetMountedSharesAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>())).ReturnsAsync([]);
        repository.Setup(r => r.GetSharesAsync(It.IsAny<CancellationToken>())).ReturnsAsync([]);
        var settings = new Mock<ICloudAccessSettingsStore>();
        settings.Setup(x => x.GetAsync()).ReturnsAsync(CloudAccessRuntimeSettings.Default());
        var authorization = new CloudAccessAuthorizationService(repository.Object, Mock.Of<IManagementAuthService>());
        var connections = Mock.Of<IStorageConnectionRepository>(
            c => c.GetAllAsync(It.IsAny<CancellationToken>()) == Task.FromResult(new List<StorageConnection>()));
        return new MountAwareFileServiceRouter(
            new CloudAccessMountResolver(repository.Object, Mock.Of<IShareRepository>(), authorization,
                new Mock<AuthenticationStateProvider>().Object, Mock.Of<IUserContextFactory>()),
            factory,
            new CloudAccessRemoteSessionOpener(repository.Object, connections, Mock.Of<IStorageConnectionProviderCatalog>()),
            new CloudAccessDirectoryCache(new MemoryCache(new MemoryCacheOptions()), settings.Object),
            repository.Object, authorization, connections);
    }
}

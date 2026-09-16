using Kaimo_File_Server.Core.Domain;
using Kaimo_File_Server.Core.Security;
using Kaimo_File_Server.Core.Services.DataServices;
using Kaimo_File_Server.Infrastructure.Clouds;
using Xunit;

namespace Kaimo_File_Server.Tests;

public sealed class CloudProviderFactoryTests
{
    [Fact]
    public void CreateOrLoad_ResolvesRegisteredProviderWithoutFactorySwitch()
    {
        var provider = new FakeProvider();
        var factory = new CloudProviderFactory([provider]);
        var folder = CreateFolder();

        var connection = factory.CreateOrLoad(Guid.NewGuid(), folder);

        Assert.Same(provider.LastConnection, connection);
        Assert.Equal("fake", Assert.Single(factory.Providers).Id);
    }

    [Fact]
    public void CreateOrLoad_ReusesConnectionWhenMutableSyncSettingsChange()
    {
        var provider = new FakeProvider();
        var factory = new CloudProviderFactory([provider]);
        var shareId = Guid.NewGuid();
        var folder = CreateFolder();
        var first = factory.CreateOrLoad(shareId, folder);

        folder.RemotePath = "/another-folder";
        folder.LastSync = DateTime.UtcNow;
        folder.Mode = SyncMode.Pull;
        var second = factory.CreateOrLoad(shareId, folder);

        Assert.Same(first, second);
        Assert.Equal(1, provider.CreatedConnections);
    }

    [Fact]
    public void CreateOrLoad_ReusesConnectionWhenProviderRotatesCredential()
    {
        var provider = new FakeProvider();
        var factory = new CloudProviderFactory([provider]);
        var shareId = Guid.NewGuid();
        var folder = new SyncedFolder("fake", new Dictionary<string, string>
        {
            ["connectionId"] = Guid.NewGuid().ToString("N"),
            ["refreshToken"] = "old-token"
        });
        var first = factory.CreateOrLoad(shareId, folder);

        folder.Data["refreshToken"] = "rotated-token";
        var second = factory.CreateOrLoad(shareId, folder);

        Assert.Same(first, second);
        Assert.Equal(1, provider.CreatedConnections);
    }

    [Fact]
    public async Task RevokeAndEvictAsync_RevokesThenRemoves()
    {
        var provider = new FakeProvider();
        var factory = new CloudProviderFactory([provider]);
        var shareId = Guid.NewGuid();
        var folder = CreateFolder();
        var first = (FakeConnection)factory.CreateOrLoad(shareId, folder);

        await factory.RevokeAndEvictAsync(shareId, folder);
        var second = factory.CreateOrLoad(shareId, folder);

        Assert.True(first.WasRevoked);
        Assert.True(first.WasClosed);
        Assert.NotSame(first, second);
        Assert.Equal(2, provider.CreatedConnections);
    }

    [Fact]
    public async Task EvictAsync_ClosesConnectionWithoutRevoking()
    {
        var provider = new FakeProvider();
        var factory = new CloudProviderFactory([provider]);
        var shareId = Guid.NewGuid();
        var folder = CreateFolder();
        var first = (FakeConnection)factory.CreateOrLoad(shareId, folder);

        await factory.EvictAsync(shareId, folder);
        var second = factory.CreateOrLoad(shareId, folder);

        Assert.True(first.WasClosed);
        Assert.False(first.WasRevoked); // eviction must never cost the user their grant
        Assert.NotSame(first, second);
    }

    [Fact]
    public async Task EvictConnectionAsync_RemovesEveryEntryForThatConnectionId()
    {
        var provider = new FakeProvider();
        var factory = new CloudProviderFactory([provider]);
        var connectionId = Guid.NewGuid();
        // Same connection id used across two different shares.
        var folderA = ConnectionFolder(connectionId);
        var folderB = ConnectionFolder(connectionId);
        var a = (FakeConnection)factory.CreateOrLoad(Guid.NewGuid(), folderA);
        var b = (FakeConnection)factory.CreateOrLoad(Guid.NewGuid(), folderB);

        await factory.EvictConnectionAsync(connectionId);

        Assert.True(a.WasClosed);
        Assert.True(b.WasClosed);
        Assert.False(a.WasRevoked);
        Assert.False(b.WasRevoked);
    }

    [Fact]
    public async Task EvictShareAsync_RemovesEveryEntryForThatShare()
    {
        var provider = new FakeProvider();
        var factory = new CloudProviderFactory([provider]);
        var shareId = Guid.NewGuid();
        var a = (FakeConnection)factory.CreateOrLoad(shareId, ConnectionFolder(Guid.NewGuid()));
        var b = (FakeConnection)factory.CreateOrLoad(shareId, ConnectionFolder(Guid.NewGuid()));
        var other = (FakeConnection)factory.CreateOrLoad(Guid.NewGuid(), ConnectionFolder(Guid.NewGuid()));

        await factory.EvictShareAsync(shareId);

        Assert.True(a.WasClosed);
        Assert.True(b.WasClosed);
        Assert.False(other.WasClosed); // a different share is untouched
    }

    [Fact]
    public void CreateOrLoad_WhenAnotherThreadWins_ClosesTheLosingConnection()
    {
        var provider = new RacingProvider();
        var factory = new CloudProviderFactory([provider]);
        provider.Bind(factory);
        var folder = CreateFolder();

        var result = factory.CreateOrLoad(Guid.NewGuid(), folder);

        // The provider built two connections; the winner is served and the loser closed
        // (never revoked — that would invalidate the credential the winner uses).
        Assert.Equal(2, provider.Created.Count);
        Assert.Same(provider.Created[1], result);      // inner (winner) is returned
        Assert.True(provider.Created[0].WasClosed);     // outer (loser) is closed
        Assert.False(provider.Created[0].WasRevoked);
        Assert.False(provider.Created[1].WasClosed);
    }

    [Fact]
    public void FullAdmin_IncludesEveryCloudSyncPermission()
    {
        Assert.Equal(
            ManagementPermission.SyncAdmin,
            ManagementPermission.FullAdmin & ManagementPermission.SyncAdmin);
    }

    private static SyncedFolder CreateFolder()
        => new("fake", new Dictionary<string, string> { ["credential"] = "secret" });

    private static SyncedFolder ConnectionFolder(Guid connectionId)
        => new("fake", new Dictionary<string, string> { ["connectionId"] = connectionId.ToString("D") });

    /// <summary>
    /// Reproduces the CreateOrLoad GetOrAdd race deterministically: the first
    /// CreateConnection reenters the factory for the same key, which wins the GetOrAdd,
    /// so the outer call's connection is the loser that must be closed.
    /// </summary>
    private sealed class RacingProvider : ICloudProvider
    {
        private CloudProviderFactory? _factory;
        private int _depth;
        public List<FakeConnection> Created { get; } = [];

        public void Bind(CloudProviderFactory factory) => _factory = factory;

        public string Id => "fake";
        public string DisplayName => "Racing Cloud";
        public string AuthorizationEndpoint => "/api/fake/connect";

        public ICloudConnection CreateConnection(Guid shareId, SyncedFolder folder)
        {
            var connection = new FakeConnection();
            Created.Add(connection);
            if (Interlocked.Increment(ref _depth) == 1)
                _factory!.CreateOrLoad(shareId, folder); // seed the cache as the "winner"
            return connection;
        }
    }

    private sealed class FakeProvider : ICloudProvider
    {
        public string Id => "fake";
        public string DisplayName => "Fake Cloud";
        public string AuthorizationEndpoint => "/api/fake/connect";
        public int CreatedConnections { get; private set; }
        public FakeConnection? LastConnection { get; private set; }

        public ICloudConnection CreateConnection(Guid shareId, SyncedFolder folder)
        {
            CreatedConnections++;
            return LastConnection = new FakeConnection();
        }
    }

    private sealed class FakeConnection : ICloudConnection
    {
        public string ServiceName => "Fake Cloud";
        public bool WasClosed { get; private set; }
        public bool WasRevoked { get; private set; }

        public ValueTask CloseAsync()
        {
            WasClosed = true;
            return ValueTask.CompletedTask;
        }

        public Task RevokeAndCloseAsync()
        {
            WasRevoked = true;
            WasClosed = true;
            return Task.CompletedTask;
        }

        public Task UploadAsync(string path, Stream data, DateTime modifiedTime, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task DownloadAsync(string path, Stream target, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task CreateDirectoryAsync(string path, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<long> GetDirectorySizeAsync(string path, CancellationToken cancellationToken = default) => Task.FromResult(0L);
        public Task<IReadOnlyList<CloudItemMeta>> ListAsync(string path, CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<CloudItemMeta>>([]);
    }
}

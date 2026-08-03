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
    public async Task DisposeConnectionAsync_AwaitsProviderCleanupAndEvictsCacheEntry()
    {
        var provider = new FakeProvider();
        var factory = new CloudProviderFactory([provider]);
        var shareId = Guid.NewGuid();
        var folder = CreateFolder();
        var first = (FakeConnection)factory.CreateOrLoad(shareId, folder);

        await factory.DisposeConnectionAsync(shareId, folder);
        var second = factory.CreateOrLoad(shareId, folder);

        Assert.True(first.WasDisposed);
        Assert.NotSame(first, second);
        Assert.Equal(2, provider.CreatedConnections);
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
        public bool WasDisposed { get; private set; }

        public Task Dispose()
        {
            WasDisposed = true;
            return Task.CompletedTask;
        }

        public Task UploadAsync(string path, Stream data, DateTime modifiedTime) => Task.CompletedTask;
        public Task DownloadAsync(string path, Stream target) => Task.CompletedTask;
        public Task CreateDirectoryAsync(string path) => Task.CompletedTask;
        public Task<long> GetDirectorySizeAsync(string path) => Task.FromResult(0L);
        public Task<IReadOnlyList<CloudItemMeta>> ListAsync(string path)
            => Task.FromResult<IReadOnlyList<CloudItemMeta>>([]);
    }
}

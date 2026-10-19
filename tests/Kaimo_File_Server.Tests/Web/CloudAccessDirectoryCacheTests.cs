using Kaimo_File_Server.Core.Domain;
using Kaimo_File_Server.Web.Services;
using Microsoft.Extensions.Caching.Memory;
using Moq;
using Xunit;

namespace Kaimo_File_Server.Tests;

public sealed class CloudAccessDirectoryCacheTests
{
    [Fact]
    public async Task GetOrCreateAsync_ReusesMetadataButReturnsIndependentObjects()
    {
        using var memory = new MemoryCache(new MemoryCacheOptions());
        var cache = CreateCache(memory);
        var shareId = Guid.NewGuid();
        var calls = 0;

        Task<List<FileMetadata>> Load()
        {
            calls++;
            return Task.FromResult(new List<FileMetadata>
            {
                new() { Name = "Reports", Path = "Reports", IsDirectory = true }
            });
        }

        var first = await cache.GetOrCreateAsync(shareId, "", Load);
        first[0].Name = "Changed locally";
        var second = await cache.GetOrCreateAsync(shareId, "", Load);

        Assert.Equal(1, calls);
        Assert.Equal("Reports", second[0].Name);
        Assert.NotSame(first[0], second[0]);
    }

    [Fact]
    public async Task InvalidateShare_ForcesReloadForEveryPath()
    {
        using var memory = new MemoryCache(new MemoryCacheOptions());
        var cache = CreateCache(memory);
        var shareId = Guid.NewGuid();
        var calls = 0;

        async Task Load(string path)
        {
            await cache.GetOrCreateAsync(shareId, path, () =>
            {
                calls++;
                return Task.FromResult(new List<FileMetadata>());
            });
        }

        await Load("");
        await Load("Documents");
        await Load("");
        cache.InvalidateShare(shareId);
        await Load("");
        await Load("Documents");

        Assert.Equal(4, calls);
    }

    [Fact]
    public async Task GetOrCreateAsync_CoalescesConcurrentProviderLoads()
    {
        using var memory = new MemoryCache(new MemoryCacheOptions());
        var cache = CreateCache(memory);
        var shareId = Guid.NewGuid();
        var calls = 0;

        async Task<List<FileMetadata>> Load()
        {
            Interlocked.Increment(ref calls);
            await Task.Delay(30);
            return [];
        }

        await Task.WhenAll(Enumerable.Range(0, 8)
            .Select(_ => cache.GetOrCreateAsync(shareId, "Documents", Load)));

        Assert.Equal(1, calls);
    }

    private static CloudAccessDirectoryCache CreateCache(IMemoryCache memory)
    {
        var settings = new Mock<ICloudAccessSettingsStore>();
        settings.Setup(x => x.GetAsync()).ReturnsAsync(CloudAccessRuntimeSettings.Default());
        return new CloudAccessDirectoryCache(memory, settings.Object);
    }
}

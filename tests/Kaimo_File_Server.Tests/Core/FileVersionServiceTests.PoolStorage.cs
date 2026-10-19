using Kaimo_File_Server.Core.Domain;
using Kaimo_File_Server.Core.Repositories;
using Kaimo_File_Server.Core.Services.File;
using System.IO.Compression;
using System.Security.Cryptography;
using Moq;
using Xunit;

namespace Kaimo_File_Server.Tests;

public partial class FileVersionServiceTests
{
    // ═══════════════════════════════════════════
    //  Disk-space guard and pool storage location
    // ═══════════════════════════════════════════

    private sealed class FakeLocator(Dictionary<Guid, string?> roots) : IVersionStorageLocator
    {
        public Task<string?> GetRootAsync(Guid shareId)
            => Task.FromResult(roots.GetValueOrDefault(shareId));

        public IReadOnlyList<string> AllRoots
            => roots.Values.OfType<string>().Distinct().ToList();
    }

    private FileVersionService CreatePoolService(IVersionStorageLocator locator, long freeBytes = 1L << 50)
        => new(_repo, _versionRoot, defaultMaxVersions: 5, timeProvider: _time,
            storageLocator: locator, diskSpaceProbe: _ => (freeBytes, 1L << 50));

    [Fact]
    public async Task CreateVersionAsync_LowDiskSpace_SkipsVersionAndWritesNoBlob()
    {
        // 100 GB volume with 1 GB free: below the reserve, so nothing may be written.
        var service = new FileVersionService(_repo, _versionRoot, timeProvider: _time,
            diskSpaceProbe: _ => (1L << 30, 100L << 30));

        var version = await service.CreateVersionAsync(ShareA, "doc.txt", ToStream("payload"));

        Assert.Null(version);
        Assert.Empty(await _repo.GetVersionsAsync(ShareA, "doc.txt"));
        Assert.Empty(Directory.GetFiles(_versionRoot, "*.bin.gz", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task CreateVersionAsync_WithLocator_StoresBlobInSharePoolNotInLegacyStore()
    {
        var poolRoot = Path.Combine(_testRoot, "pool", ".kaimo-versions");
        var service = CreatePoolService(new FakeLocator(new() { [ShareA] = poolRoot }));
        var data = System.Text.Encoding.UTF8.GetBytes("pooled");

        var version = await service.CreateVersionAsync(ShareA, "doc.txt", ToStream(data));

        Assert.NotNull(version);
        Assert.True(File.Exists(Path.Combine(poolRoot, version.StoragePath)));
        Assert.Empty(Directory.GetFiles(_versionRoot, "*", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task CreateVersionAsync_ShareOutsideAnyPool_SkipsVersion()
    {
        var service = CreatePoolService(new FakeLocator(new() { [ShareA] = null }));

        Assert.Null(await service.CreateVersionAsync(ShareA, "doc.txt", ToStream("x")));
        Assert.Empty(await _repo.GetVersionsAsync(ShareA, "doc.txt"));
    }

    [Fact]
    public async Task ReconcileStorageAsync_MovesLegacyBlobsIntoPoolAndDrainsLegacyStore()
    {
        // A version written by the pre-upgrade layout (fixed app-data root).
        var legacyVersion = await _sut.CreateVersionAsync(ShareA, "doc.txt", ToStream("legacy content"));
        Assert.NotNull(legacyVersion);

        var poolRoot = Path.Combine(_testRoot, "pool", ".kaimo-versions");
        var service = CreatePoolService(new FakeLocator(new() { [ShareA] = poolRoot }));

        // Readable through the legacy fallback before reconciliation…
        await using (var before = await service.ReadVersionAsync(
                         ShareA, "doc.txt", legacyVersion.SnapshotTimestampUtc))
            Assert.Equal("legacy content", new StreamReader(before).ReadToEnd());

        await service.ReconcileStorageAsync();

        // …and afterwards from the pool, with the legacy store gone.
        Assert.True(File.Exists(Path.Combine(poolRoot, legacyVersion.StoragePath)));
        Assert.False(Directory.Exists(_versionRoot));
        await using var after = await service.ReadVersionAsync(
            ShareA, "doc.txt", legacyVersion.SnapshotTimestampUtc);
        Assert.Equal("legacy content", new StreamReader(after).ReadToEnd());
    }

    [Fact]
    public void PoolVersionStorageLocator_PlacesVersionsInThePoolOfTheShare()
    {
        var pool1 = Path.Combine(_testRoot, "storage", "pool01");
        var pool2 = Path.Combine(_testRoot, "storage", "pool02");
        var locator = new Kaimo_File_Server.Infrastructure.Services.PoolVersionStorageLocator(
            Mock.Of<IShareRepository>(), [pool1, pool2]);

        Assert.Equal(Path.Combine(pool2, ".kaimo-versions"),
            locator.RootForSharePath(Path.Combine(pool2, "appserver")));
        // A share that is a pool root itself would get the folder inside the share.
        Assert.Null(locator.RootForSharePath(pool1));
        Assert.Null(locator.RootForSharePath(Path.Combine(_testRoot, "elsewhere", "share")));
        Assert.Equal(2, locator.AllRoots.Count);
    }
}

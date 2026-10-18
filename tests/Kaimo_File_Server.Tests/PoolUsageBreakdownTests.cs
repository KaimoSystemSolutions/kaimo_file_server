using Kaimo_File_Server.Infrastructure.Services;
using Xunit;

namespace Kaimo_File_Server.Tests;

/// <summary>
/// Covers the "usage by origin" breakdown of the storage tab: the pool scanner that splits
/// a pool into files / recycle bins / versions / other Kaimo data, the mountinfo parser
/// that groups pools living on the same disk, and the single-flight scan cache.
/// </summary>
public sealed class PoolUsageBreakdownTests : IDisposable
{
    private const long Block = PoolUsageScanner.AllocationUnit;
    private readonly string _pool = Path.Combine(Path.GetTempPath(), "kaimo-pool-usage-" + Guid.NewGuid().ToString("N"));

    public PoolUsageBreakdownTests() => Directory.CreateDirectory(_pool);

    public void Dispose()
    {
        try { Directory.Delete(_pool, recursive: true); } catch (IOException) { }
    }

    private void WriteFile(string relativePath, int bytes)
    {
        var full = Path.Combine(_pool, relativePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllBytes(full, new byte[bytes]);
    }

    // ─────────────────────────── Scanner ───────────────────────────

    [Fact]
    public void Scan_ClassifiesEveryTopLevelEntry()
    {
        WriteFile("docs/a.txt", 10);
        WriteFile("docs/sub/b.txt", 10);
        WriteFile("docs/.RECYCLE_BIN/old.txt", 10);
        WriteFile("users/u1/notes.txt", 10);
        WriteFile("users/u1/.RECYCLE_BIN/gone.txt", 10);
        WriteFile(".kaimo-versions/ab/blob", 10);
        WriteFile(".kaimo-moving-123/x", 10);
        WriteFile("loose-root-file.bin", 10);

        var result = PoolUsageScanner.Scan(_pool, Path.Combine(_pool, "users"));

        Assert.Equal(3 * Block, result.FileBytes);
        Assert.Equal(2 * Block, result.RecycleBinBytes);
        Assert.Equal(1 * Block, result.VersionBytes);
        Assert.Equal(2 * Block, result.OtherServerBytes);
        Assert.Equal(8 * Block, result.KaimoBytes);
    }

    [Fact]
    public void Scan_NestedRecycleBinNameInsideShare_CountsAsFiles()
    {
        WriteFile("docs/project/.RECYCLE_BIN/file.txt", 10);

        var result = PoolUsageScanner.Scan(_pool, homesSharePath: null);

        Assert.Equal(Block, result.FileBytes);
        Assert.Equal(0, result.RecycleBinBytes);
    }

    [Fact]
    public void Scan_PerUserRecycleBin_OnlyRecognizedInsideTheHomesShare()
    {
        // Same layout as a home folder, but in a regular share: <x>/.RECYCLE_BIN there is user data.
        WriteFile("team/u1/.RECYCLE_BIN/file.txt", 10);
        WriteFile("users/u1/.RECYCLE_BIN/file.txt", 10);

        var result = PoolUsageScanner.Scan(_pool, Path.Combine(_pool, "users"));

        Assert.Equal(Block, result.FileBytes);
        Assert.Equal(Block, result.RecycleBinBytes);
    }

    [Fact]
    public void Scan_RoundsFileSizesUpToAllocationUnit()
    {
        WriteFile("docs/empty.txt", 0);
        WriteFile("docs/one.txt", 1);
        WriteFile("docs/exact.bin", (int)Block);
        WriteFile("docs/over.bin", (int)Block + 1);

        var result = PoolUsageScanner.Scan(_pool, homesSharePath: null);

        Assert.Equal(0 + Block + Block + 2 * Block, result.FileBytes);
    }

    [Fact]
    public void Scan_EmptyPool_ReturnsZeros()
    {
        var result = PoolUsageScanner.Scan(_pool, homesSharePath: null);

        Assert.Equal(0, result.KaimoBytes);
    }

    [Fact]
    public void Scan_MissingPool_ReturnsZeros()
    {
        var result = PoolUsageScanner.Scan(Path.Combine(_pool, "does-not-exist"), homesSharePath: null);

        Assert.Equal(0, result.KaimoBytes);
    }

    [Fact]
    public void Scan_DoesNotFollowSymlinks()
    {
        var outside = Path.Combine(Path.GetTempPath(), "kaimo-pool-outside-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(outside);
        try
        {
            File.WriteAllBytes(Path.Combine(outside, "big.bin"), new byte[10]);
            Directory.CreateDirectory(Path.Combine(_pool, "docs"));
            try
            {
                Directory.CreateSymbolicLink(Path.Combine(_pool, "docs", "link"), outside);
                Directory.CreateSymbolicLink(Path.Combine(_pool, "toplink"), outside);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return; // No symlink privilege on this machine (Windows without developer mode).
            }

            var result = PoolUsageScanner.Scan(_pool, homesSharePath: null);

            Assert.Equal(0, result.KaimoBytes);
        }
        finally
        {
            Directory.Delete(outside, recursive: true);
        }
    }

    // ─────────────────────────── mountinfo grouping ───────────────────────────

    private static readonly string[] MountInfoSample =
    [
        "571 470 0:52 / / rw,relatime master:248 - overlay overlay rw,lowerdir=/x",
        "590 571 8:1 /srv/kaimo/data/storage/pool01 /data/storage/pool01 rw,relatime - ext4 /dev/sda1 rw",
        "591 571 8:1 /srv/kaimo/data/storage/pool02 /data/storage/pool02 rw,relatime - ext4 /dev/sda1 rw",
        "592 571 8:17 /mnt/big /data/storage/pool\\040three rw,relatime - xfs /dev/sdb1 rw",
    ];

    [Fact]
    public void ParseMountInfo_MapsMountPointToDeviceId_AndUnescapesSpaces()
    {
        var mounts = SystemInfoService.ParseMountInfo(MountInfoSample);

        Assert.Equal("0:52", mounts["/"]);
        Assert.Equal("8:1", mounts["/data/storage/pool01"]);
        Assert.Equal("8:1", mounts["/data/storage/pool02"]);
        Assert.Equal("8:17", mounts["/data/storage/pool three"]);
    }

    [Theory]
    [InlineData("/data/storage/pool01", "8:1")]
    [InlineData("/data/storage/pool01/", "8:1")]
    [InlineData("/data/storage/pool three", "8:17")]
    [InlineData("/data/storage/pool01-not-a-mount", "0:52")] // falls back to the enclosing "/" mount
    public void ResolveDeviceId_UsesLongestEnclosingMount(string path, string expected)
    {
        var mounts = SystemInfoService.ParseMountInfo(MountInfoSample);

        Assert.Equal(expected, SystemInfoService.ResolveDeviceId(path, mounts));
    }

    [Fact]
    public void ResolveDeviceId_WithoutMountInfo_ReturnsNull()
    {
        Assert.Null(SystemInfoService.ResolveDeviceId("/data/storage/pool01", new Dictionary<string, string>()));
    }

    // ─────────────────────────── Scan cache ───────────────────────────

    [Fact]
    public async Task ScanAsync_ConcurrentCallsForOnePool_ShareASingleScan()
    {
        var calls = 0;
        using var release = new ManualResetEventSlim();
        var service = new PoolUsageService((_, _) =>
        {
            Interlocked.Increment(ref calls);
            release.Wait(TimeSpan.FromSeconds(10));
            return new PoolUsageBreakdown(1, 0, 0, 0, DateTime.UtcNow);
        });

        var first = service.ScanAsync("/pool", null);
        var second = service.ScanAsync("/pool", null);
        release.Set();
        await Task.WhenAll(first, second);

        Assert.Equal(1, calls);
        Assert.Same(await first, await second);
    }

    [Fact]
    public async Task ScanAsync_CachesResult_AndLaterScanRescans()
    {
        var calls = 0;
        var service = new PoolUsageService((_, _) =>
            new PoolUsageBreakdown(Interlocked.Increment(ref calls), 0, 0, 0, DateTime.UtcNow));

        Assert.Null(service.GetCached("/pool"));

        var first = await service.ScanAsync("/pool", null);
        Assert.Same(first, service.GetCached("/pool"));

        var second = await service.ScanAsync("/pool", null);
        Assert.Equal(2, calls);
        Assert.Same(second, service.GetCached("/pool"));
    }

    [Fact]
    public async Task ScanAsync_WhenScanThrows_KeepsPreviousCacheAndAllowsRetry()
    {
        var fail = false;
        var service = new PoolUsageService((_, _) => fail
            ? throw new IOException("disk gone")
            : new PoolUsageBreakdown(5, 0, 0, 0, DateTime.UtcNow));

        var good = await service.ScanAsync("/pool", null);
        fail = true;
        await Assert.ThrowsAsync<IOException>(() => service.ScanAsync("/pool", null));

        Assert.Same(good, service.GetCached("/pool"));
        fail = false;
        Assert.Equal(5, (await service.ScanAsync("/pool", null)).FileBytes);
    }
}

using Kaimo_File_Server.Core.Domain;
using Kaimo_File_Server.Core.Repositories;
using Kaimo_File_Server.Core.Services.File;
using Kaimo_File_Server.Infrastructure.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Kaimo_File_Server.Tests;

/// <summary>
/// <see cref="VersionRetentionSweeper"/> against a real pool folder with real blobs and the
/// in-memory version repository; the fake clock ages versions between writes and sweeps.
/// </summary>
public sealed class VersionRetentionSweeperTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "kaimo-sweep-" + Guid.NewGuid().ToString("N"));
    private readonly string _versionRoot;
    private readonly ShareDefinition _share;
    private readonly MockFileVersionRepository _repo = new();
    private readonly MutableTimeProvider _time = new();
    private readonly Mock<IShareRepository> _shares = new();
    private readonly PolicyProvider _policy = new();
    private long _poolCapacity = 1L << 50;

    private sealed class PolicyProvider : IVersioningPolicyProvider
    {
        public VersioningPolicy Policy { get; set; } = VersioningPolicy.Default();
        public Task<VersioningPolicy> GetAsync() => Task.FromResult(Policy.Clone());
    }

    private sealed class SingleRootLocator(Guid shareId, string root) : IVersionStorageLocator
    {
        public Task<string?> GetRootAsync(Guid id) => Task.FromResult(id == shareId ? root : null);
        public IReadOnlyList<string> AllRoots => [root];
    }

    public VersionRetentionSweeperTests()
    {
        var pool = Path.Combine(_root, "pool");
        _versionRoot = Path.Combine(pool, ".kaimo-versions");
        _share = new ShareDefinition("docs", Path.Combine(pool, "docs"));
        Directory.CreateDirectory(_share.Path);
        _shares.Setup(s => s.GetByIdAsync(_share.Id)).ReturnsAsync(_share);
        Service = new FileVersionService(_repo, Path.Combine(_root, "legacy"), timeProvider: _time,
            storageLocator: new SingleRootLocator(_share.Id, _versionRoot),
            diskSpaceProbe: _ => (1L << 50, _poolCapacity), policyProvider: _policy);
    }

    private FileVersionService Service { get; }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    private VersionRetentionSweeper CreateSweeper() => new(
        _repo, Service, _shares.Object, _policy, new SingleRootLocator(_share.Id, _versionRoot),
        NullLogger<VersionRetentionSweeper>.Instance, _time,
        diskSpaceProbe: _ => (1L << 50, _poolCapacity));

    /// <summary>Writes the live file and snapshots it, as an upload would.</summary>
    private async Task WriteAsync(string path, byte[] content)
    {
        var full = Path.Combine(_share.Path, path);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        await File.WriteAllBytesAsync(full, content);
        await Service.CaptureVersionAsync(_share.Id, path, new MemoryStream(content));
        _time.Advance(TimeSpan.FromSeconds(1));
    }

    private Task WriteAsync(string path, string content) => WriteAsync(path, System.Text.Encoding.UTF8.GetBytes(content));

    private static byte[] Random(int size)
    {
        var data = new byte[size];
        System.Random.Shared.NextBytes(data);
        return data;
    }

    private int BlobCount() => Directory.Exists(_versionRoot)
        ? Directory.GetFiles(_versionRoot, "*.bin.gz", SearchOption.AllDirectories).Length
        : 0;

    [Fact]
    public async Task AgeRetention_UnchangedFile_KeepsOnlyTheNewestVersion()
    {
        await WriteAsync("a.txt", "v1");
        await WriteAsync("a.txt", "v2");
        _time.Advance(TimeSpan.FromDays(120));

        await CreateSweeper().RunOnceAsync();

        var remaining = Assert.Single(await _repo.GetVersionsAsync(_share.Id, "a.txt"));
        Assert.Equal(2, remaining.VersionNumber);
        Assert.Equal(1, BlobCount());
    }

    [Fact]
    public async Task Orphan_FileGoneAfterGrace_RemovesHistoryAndBlobs()
    {
        await WriteAsync("keep.txt", "keep");
        await WriteAsync("gone.txt", "gone");
        File.Delete(Path.Combine(_share.Path, "gone.txt"));
        _time.Advance(TimeSpan.FromHours(2));

        await CreateSweeper().RunOnceAsync();

        Assert.Empty(await _repo.GetVersionsAsync(_share.Id, "gone.txt"));
        Assert.Single(await _repo.GetVersionsAsync(_share.Id, "keep.txt"));
        Assert.Equal(1, BlobCount());
    }

    [Fact]
    public async Task Orphan_FileGoneWithinGrace_IsKept()
    {
        await WriteAsync("keep.txt", "keep");
        await WriteAsync("moving.txt", "data");
        File.Delete(Path.Combine(_share.Path, "moving.txt"));

        await CreateSweeper().RunOnceAsync();

        Assert.Single(await _repo.GetVersionsAsync(_share.Id, "moving.txt"));
    }

    [Fact]
    public async Task Orphan_ShareRootMissing_KeepsEverything()
    {
        await WriteAsync("a.txt", "a");
        Directory.Delete(_share.Path, recursive: true);
        _time.Advance(TimeSpan.FromHours(2));

        await CreateSweeper().RunOnceAsync();

        Assert.Single(await _repo.GetVersionsAsync(_share.Id, "a.txt"));
    }

    [Fact]
    public async Task Orphan_ShareRootEmpty_KeepsEverything()
    {
        // An unmounted pool often leaves an empty mount-point folder behind.
        await WriteAsync("a.txt", "a");
        await WriteAsync("sub/b.txt", "b");
        Directory.Delete(_share.Path, recursive: true);
        Directory.CreateDirectory(_share.Path);
        _time.Advance(TimeSpan.FromHours(2));

        await CreateSweeper().RunOnceAsync();

        Assert.Single(await _repo.GetVersionsAsync(_share.Id, "a.txt"));
        Assert.Single(await _repo.GetVersionsAsync(_share.Id, "sub/b.txt"));
    }

    [Fact]
    public async Task Orphan_FileExistsWithDifferentCase_IsKept()
    {
        await WriteAsync("Report.TXT", "r");
        File.Move(Path.Combine(_share.Path, "Report.TXT"), Path.Combine(_share.Path, "report.txt"));
        _time.Advance(TimeSpan.FromHours(2));

        await CreateSweeper().RunOnceAsync();

        Assert.Single(await _repo.GetVersionsAsync(_share.Id, "Report.TXT"));
    }

    [Fact]
    public async Task LegacyAndUnknownShareRows_AreRemoved()
    {
        await WriteAsync("a.txt", "a");
        var unknown = Guid.NewGuid();
        await _repo.CreateAsync(new FileVersion(Guid.Empty, "legacy.txt", DateTime.UtcNow, "AA/BB/L.bin.gz", "L", 1, null, 1));
        await _repo.CreateAsync(new FileVersion(unknown, "old.txt", DateTime.UtcNow, "AA/BB/U.bin.gz", "U", 1, null, 1));

        await CreateSweeper().RunOnceAsync();

        Assert.Empty(await _repo.GetVersionsAsync(Guid.Empty, "legacy.txt"));
        Assert.Empty(await _repo.GetVersionsAsync(unknown, "old.txt"));
        Assert.Single(await _repo.GetVersionsAsync(_share.Id, "a.txt"));
    }

    [Fact]
    public async Task Budget_Exceeded_RemovesOldestVersionsButKeepsNewestPerFile()
    {
        for (var round = 0; round < 3; round++)
            foreach (var name in new[] { "a.bin", "b.bin", "c.bin" })
                await WriteAsync(name, Random(4096));
        // 9 incompressible 4 KB blobs; a 15 % budget of 100 KB fits only three of them.
        _poolCapacity = 100 * 1024;

        await CreateSweeper().RunOnceAsync();

        foreach (var name in new[] { "a.bin", "b.bin", "c.bin" })
            Assert.Equal(3, Assert.Single(await _repo.GetVersionsAsync(_share.Id, name)).VersionNumber);
        Assert.Equal(3, BlobCount());
    }

    [Fact]
    public async Task Budget_RemovesOnlyAsMuchAsNeeded()
    {
        await WriteAsync("a.bin", Random(4096));
        await WriteAsync("a.bin", Random(4096));
        await WriteAsync("a.bin", Random(4096));
        // Budget 15 % of 70 KB ≈ 10.5 KB: two of the three blobs fit.
        _poolCapacity = 70 * 1024;

        await CreateSweeper().RunOnceAsync();

        var remaining = await _repo.GetVersionsAsync(_share.Id, "a.bin");
        Assert.Equal([3, 2], remaining.Select(v => v.VersionNumber));
    }

    [Fact]
    public async Task Budget_NothingPrunable_Stops()
    {
        await WriteAsync("a.bin", Random(4096));
        _poolCapacity = 1024;

        await CreateSweeper().RunOnceAsync();

        Assert.Single(await _repo.GetVersionsAsync(_share.Id, "a.bin"));
    }

    [Fact]
    public async Task Budget_Disabled_RemovesNothing()
    {
        await WriteAsync("a.bin", Random(4096));
        await WriteAsync("a.bin", Random(4096));
        _poolCapacity = 1024;
        _policy.Policy = new VersioningPolicy { PoolBudgetPercent = 0 };

        await CreateSweeper().RunOnceAsync();

        Assert.Equal(2, (await _repo.GetVersionsAsync(_share.Id, "a.bin")).Count);
    }

    [Fact]
    public async Task ManyPaths_AreProcessedAcrossPages()
    {
        for (var i = 0; i < 7; i++)
        {
            await WriteAsync($"f{i}.txt", "v1");
            await WriteAsync($"f{i}.txt", "v2");
        }
        _time.Advance(TimeSpan.FromDays(120));

        await new VersionRetentionSweeper(
            _repo, Service, _shares.Object, _policy, new SingleRootLocator(_share.Id, _versionRoot),
            NullLogger<VersionRetentionSweeper>.Instance, _time,
            diskSpaceProbe: _ => (1L << 50, _poolCapacity), pageSize: 3).RunOnceAsync();

        for (var i = 0; i < 7; i++)
            Assert.Single(await _repo.GetVersionsAsync(_share.Id, $"f{i}.txt"));
    }
}

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

    /// <summary>One sweep that notices missing files, then one after the grace period.</summary>
    private async Task SweepPastGraceAsync()
    {
        await CreateSweeper().RunOnceAsync();
        _time.Advance(VersionRetentionSweeper.OrphanGrace + TimeSpan.FromHours(1));
        await CreateSweeper().RunOnceAsync();
    }

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

        await SweepPastGraceAsync();

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
        _time.Advance(VersionRetentionSweeper.OrphanGrace - TimeSpan.FromHours(1));
        await CreateSweeper().RunOnceAsync();

        Assert.Single(await _repo.GetVersionsAsync(_share.Id, "moving.txt"));
    }

    [Fact]
    public async Task Orphan_GraceCountsFromDisappearance_NotFromTheLastSnapshot()
    {
        // A file untouched for a year and then deleted outside the server still gets the
        // full grace period; the age of its newest snapshot says nothing about the deletion.
        await WriteAsync("keep.txt", "keep");
        await WriteAsync("old.txt", "old");
        _time.Advance(TimeSpan.FromDays(365));
        File.Delete(Path.Combine(_share.Path, "old.txt"));

        await CreateSweeper().RunOnceAsync();
        Assert.Single(await _repo.GetVersionsAsync(_share.Id, "old.txt"));

        _time.Advance(VersionRetentionSweeper.OrphanGrace + TimeSpan.FromHours(1));
        await CreateSweeper().RunOnceAsync();
        Assert.Empty(await _repo.GetVersionsAsync(_share.Id, "old.txt"));
    }

    [Fact]
    public async Task Orphan_FileReappears_RestartsTheGracePeriod()
    {
        await WriteAsync("keep.txt", "keep");
        await WriteAsync("back.txt", "data");
        var full = Path.Combine(_share.Path, "back.txt");
        File.Delete(full);
        await CreateSweeper().RunOnceAsync();

        _time.Advance(VersionRetentionSweeper.OrphanGrace - TimeSpan.FromDays(1));
        await File.WriteAllTextAsync(full, "data");
        await CreateSweeper().RunOnceAsync();
        File.Delete(full);
        _time.Advance(TimeSpan.FromDays(2));
        await CreateSweeper().RunOnceAsync();

        Assert.Single(await _repo.GetVersionsAsync(_share.Id, "back.txt"));
    }

    [Fact]
    public async Task Orphan_FirstMissingTime_IsPersistedSoARestartKeepsIt()
    {
        // The sweeper is scoped per run and the Host restarts on every update: the time a
        // file was first found missing lives in the database, not in the process.
        await WriteAsync("keep.txt", "keep");
        await WriteAsync("gone.txt", "gone");
        File.Delete(Path.Combine(_share.Path, "gone.txt"));
        var firstSeen = _time.GetUtcNow().UtcDateTime;

        await CreateSweeper().RunOnceAsync();

        Assert.Equal(firstSeen, (await _repo.GetMissingSinceAsync(_share.Id))["gone.txt"]);
        _time.Advance(VersionRetentionSweeper.OrphanGrace + TimeSpan.FromHours(1));
        await CreateSweeper().RunOnceAsync();
        Assert.Empty(await _repo.GetMissingSinceAsync(_share.Id));

        Assert.Empty(await _repo.GetVersionsAsync(_share.Id, "gone.txt"));
    }

    [Fact]
    public async Task Orphan_ShareRootMissing_KeepsEverything()
    {
        await WriteAsync("a.txt", "a");
        Directory.Delete(_share.Path, recursive: true);

        await SweepPastGraceAsync();

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

        await SweepPastGraceAsync();

        Assert.Single(await _repo.GetVersionsAsync(_share.Id, "a.txt"));
        Assert.Single(await _repo.GetVersionsAsync(_share.Id, "sub/b.txt"));
    }

    [Fact]
    public async Task Orphan_FileExistsWithDifferentCase_IsKept()
    {
        await WriteAsync("Report.TXT", "r");
        File.Move(Path.Combine(_share.Path, "Report.TXT"), Path.Combine(_share.Path, "report.txt"));

        await SweepPastGraceAsync();

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
    public async Task Budget_Exceeded_RemovesOldestHistoryButKeepsNewestPerFile()
    {
        for (var round = 0; round < 3; round++)
            foreach (var name in new[] { "a.bin", "b.bin", "c.bin" })
                await WriteAsync(name, Random(4096));
        // 6 incompressible 4 KB history blobs (the 3 newest do not count); a 10 % budget
        // of 150 KB fits three of them, so the oldest round goes.
        _poolCapacity = 150 * 1024;

        await CreateSweeper().RunOnceAsync();

        foreach (var name in new[] { "a.bin", "b.bin", "c.bin" })
            Assert.Equal([3, 2], (await _repo.GetVersionsAsync(_share.Id, name)).Select(v => v.VersionNumber));
        Assert.Equal(6, BlobCount());
    }

    [Fact]
    public async Task Budget_NewestVersionsDoNotCount()
    {
        // The newest version duplicates the live file and can never be pruned; counting it
        // would wipe every history as soon as the live data alone exceeds the budget.
        for (var i = 0; i < 5; i++)
            await WriteAsync($"f{i}.bin", Random(4096));
        await WriteAsync("a.bin", Random(4096));
        await WriteAsync("a.bin", Random(4096));
        // 7 blobs ≈ 28 KB in total, but only one 4 KB history blob; budget 10 KB.
        _poolCapacity = 100 * 1024;

        await CreateSweeper().RunOnceAsync();

        Assert.Equal(2, (await _repo.GetVersionsAsync(_share.Id, "a.bin")).Count);
    }

    [Fact]
    public async Task Budget_UnreferencedBlobsDoNotCount()
    {
        // Leftovers no row references are removed by reconciliation, not by pruning history.
        await WriteAsync("a.bin", Random(4096));
        await WriteAsync("a.bin", Random(4096));
        var junk = Path.Combine(_versionRoot, "FF", "FF", "FFFF.bin.gz");
        Directory.CreateDirectory(Path.GetDirectoryName(junk)!);
        await File.WriteAllBytesAsync(junk, Random(20 * 1024));
        _poolCapacity = 100 * 1024;

        await CreateSweeper().RunOnceAsync();

        Assert.Equal(2, (await _repo.GetVersionsAsync(_share.Id, "a.bin")).Count);
    }

    [Fact]
    public async Task Budget_RemovesOnlyAsMuchAsNeeded()
    {
        await WriteAsync("a.bin", Random(4096));
        await WriteAsync("a.bin", Random(4096));
        await WriteAsync("a.bin", Random(4096));
        // Two 4 KB history blobs; budget 10 % of 60 KB = 6 KB: one of them fits.
        _poolCapacity = 60 * 1024;

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

    private VersionRetentionSweeper CreateSweeper(int pageSize) => new(
        _repo, Service, _shares.Object, _policy, new SingleRootLocator(_share.Id, _versionRoot),
        NullLogger<VersionRetentionSweeper>.Instance, _time,
        diskSpaceProbe: _ => (1L << 50, _poolCapacity), pageSize: pageSize);

    [Fact]
    public async Task Sweep_DeletesInBoundedBatches_AndLoadsHistoriesPerPage()
    {
        // The first sweep after an upgrade may remove millions of rows: never in one call.
        for (var i = 0; i < 7; i++)
            for (var v = 0; v < 3; v++)
                await WriteAsync($"f{i}.txt", $"v{v}");
        _time.Advance(TimeSpan.FromDays(120));
        _repo.ResetCallCounters();

        await CreateSweeper(pageSize: 3).RunOnceAsync();

        Assert.Equal(0, _repo.SinglePathHistoryLoads);
        Assert.Equal(14, _repo.DeleteByIdsBatchSizes.Sum());
        Assert.All(_repo.DeleteByIdsBatchSizes, size => Assert.InRange(size, 1, 3));
        for (var i = 0; i < 7; i++)
            Assert.Single(await _repo.GetVersionsAsync(_share.Id, $"f{i}.txt"));
    }

    [Fact]
    public async Task Orphans_AreDeletedInBoundedBatches()
    {
        await WriteAsync("keep.txt", "keep");
        for (var i = 0; i < 5; i++)
        {
            await WriteAsync($"gone{i}.txt", "a");
            await WriteAsync($"gone{i}.txt", "b");
            File.Delete(Path.Combine(_share.Path, $"gone{i}.txt"));
        }
        await CreateSweeper(pageSize: 3).RunOnceAsync();
        _time.Advance(VersionRetentionSweeper.OrphanGrace + TimeSpan.FromHours(1));
        _repo.ResetCallCounters();

        await CreateSweeper(pageSize: 3).RunOnceAsync();

        Assert.Equal(10, _repo.DeleteByIdsBatchSizes.Sum());
        Assert.All(_repo.DeleteByIdsBatchSizes, size => Assert.InRange(size, 1, 3));
        for (var i = 0; i < 5; i++)
            Assert.Empty(await _repo.GetVersionsAsync(_share.Id, $"gone{i}.txt"));
    }
}

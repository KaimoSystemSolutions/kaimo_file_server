using Kaimo_File_Server.Core.Domain;
using Kaimo_File_Server.Core.Domain.ClientSync;
using Kaimo_File_Server.Core.Repositories;
using Kaimo_File_Server.Core.Services.File;
using Kaimo_File_Server.Infrastructure.Configuration;
using Kaimo_File_Server.Infrastructure.Services;
using Kaimo_File_Server.Tests.Infrastructure;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Kaimo_File_Server.Tests;

/// <summary>
/// <see cref="RecycleBinJanitor"/> against a real temp directory and the real tracking table
/// (SQLite). Deletion times come from the janitor's own first-seen bookkeeping, so the fake
/// clock is advanced between runs to age entries.
/// </summary>
public sealed class RecycleBinJanitorTests : IDisposable
{
    private const string Bin = ".RECYCLE_BIN";

    private readonly string _root = Path.Combine(Path.GetTempPath(), "kaimo-janitor-" + Guid.NewGuid().ToString("N"));
    private readonly SqliteConnection _connection = new("DataSource=:memory:");
    private readonly TestDbContextFactory _dbFactory;
    private readonly FakeTime _time = new(new DateTimeOffset(2026, 10, 1, 12, 0, 0, TimeSpan.Zero));
    private readonly Mock<IAclRepository> _acls = new();
    private readonly Mock<IFileVersionService> _versions = new();
    private readonly List<FileChangeLogEntry> _changes = [];
    private long _globalMax;
    private readonly RecycleBinJanitor _sut;

    public RecycleBinJanitorTests()
    {
        Directory.CreateDirectory(_root);
        _connection.Open();
        _dbFactory = new TestDbContextFactory(_connection);
        using (var db = _dbFactory.CreateDbContext())
            db.Database.EnsureCreated();

        var config = new Mock<IConfigRepository>();
        config.Setup(c => c.GetAsync(RecycleBinJanitor.GlobalMaxSizeConfigKey, It.IsAny<long>()))
            .ReturnsAsync(() => _globalMax);
        var changeLog = new Mock<IFileChangeLogRepository>();
        changeLog.Setup(r => r.AppendAsync(It.IsAny<FileChangeLogEntry>(), It.IsAny<CancellationToken>()))
            .Returns((FileChangeLogEntry e, CancellationToken _) => { _changes.Add(e); return Task.CompletedTask; });

        _sut = new RecycleBinJanitor(
            _dbFactory, config.Object, _acls.Object, _versions.Object, changeLog.Object,
            NullLogger<RecycleBinJanitor>.Instance, _time);
    }

    public void Dispose()
    {
        _connection.Dispose();
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    // ── helpers ──

    private async Task<ShareDefinition> AddShareAsync(
        string name = "docs", int? retentionDays = null,
        RecycleLimitMode mode = RecycleLimitMode.Inherit, long? maxBytes = null,
        bool enabled = true, bool recycle = true, bool homes = false)
    {
        var share = new ShareDefinition(name, Path.Combine(_root, name), isEnabled: enabled, isRecycleEnabled: recycle)
        {
            IsUserHomes = homes,
            RecycleRetentionDays = retentionDays,
            RecycleMaxSizeMode = mode,
            RecycleMaxSizeBytes = maxBytes
        };
        Directory.CreateDirectory(share.Path);
        await using var db = _dbFactory.CreateDbContext();
        db.ShareDefinitions.Add(share);
        await db.SaveChangesAsync();
        return share;
    }

    private static string Write(ShareDefinition share, string relativePath, int size = 100)
    {
        var full = Path.Combine(share.Path, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllBytes(full, new byte[size]);
        return full;
    }

    private static bool Exists(ShareDefinition share, string relativePath)
    {
        var full = Path.Combine(share.Path, relativePath);
        return File.Exists(full) || Directory.Exists(full);
    }

    private Task RunAsync() => _sut.RunOnceAsync(CancellationToken.None);

    private void Advance(TimeSpan by) => _time.Advance(by);

    private async Task<List<RecycleBinEntry>> RowsAsync()
    {
        await using var db = _dbFactory.CreateDbContext();
        return await db.RecycleBinEntries.AsNoTracking().ToListAsync();
    }

    // ── retention ──

    [Fact]
    public async Task Retention_PurgesOldEntries_KeepsYoungOnes()
    {
        var share = await AddShareAsync(retentionDays: 7);
        Write(share, $"{Bin}/old.txt");
        await RunAsync();

        Advance(TimeSpan.FromDays(5));
        Write(share, $"{Bin}/young.txt");
        await RunAsync();

        Advance(TimeSpan.FromDays(3));   // old: 8 days, young: 3 days
        await RunAsync();

        Assert.False(Exists(share, $"{Bin}/old.txt"));
        Assert.True(Exists(share, $"{Bin}/young.txt"));
        Assert.True(Directory.Exists(Path.Combine(share.Path, Bin)));
        Assert.Equal([$"{Bin}/young.txt"], (await RowsAsync()).Select(r => r.Path));
    }

    [Fact]
    public async Task NoRetention_NothingIsPurgedByAge()
    {
        var share = await AddShareAsync();
        Write(share, $"{Bin}/old.txt");
        await RunAsync();

        Advance(TimeSpan.FromDays(5000));
        await RunAsync();

        Assert.True(Exists(share, $"{Bin}/old.txt"));
    }

    [Fact]
    public async Task FolderMovedInWhole_OldChildrenAreNotPurgedEarly()
    {
        var share = await AddShareAsync(retentionDays: 7);
        var child = Write(share, "projects/a/file.txt");
        File.SetLastWriteTimeUtc(child, new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        Directory.CreateDirectory(Path.Combine(share.Path, Bin));
        Directory.Move(Path.Combine(share.Path, "projects"), Path.Combine(share.Path, Bin, "projects"));

        await RunAsync();
        Advance(TimeSpan.FromDays(6));
        await RunAsync();

        Assert.True(Exists(share, $"{Bin}/projects/a/file.txt"));
    }

    [Fact]
    public async Task CtimeChange_ResetsFirstSeen()
    {
        var share = await AddShareAsync(retentionDays: 7);
        var file = Write(share, $"{Bin}/again.txt");
        await RunAsync();

        Advance(TimeSpan.FromDays(10));
        // Restore + re-delete (or any metadata change) moves the change time.
        File.SetLastWriteTimeUtc(file, new DateTime(2001, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        await RunAsync();

        Assert.True(Exists(share, $"{Bin}/again.txt"));
        Assert.Equal(_time.GetUtcNow().UtcDateTime, (await RowsAsync()).Single(r => r.Path == $"{Bin}/again.txt").FirstSeenUtc);
    }

    [Fact]
    public async Task RemovedFromBin_RowIsDropped()
    {
        var share = await AddShareAsync();
        var file = Write(share, $"{Bin}/restored.txt");
        await RunAsync();
        Assert.Single(await RowsAsync());

        File.Move(file, Path.Combine(share.Path, "restored.txt"));
        await RunAsync();

        Assert.Empty(await RowsAsync());
    }

    // ── size limit ──

    [Fact]
    public async Task MaxSize_PurgesOldestFirstUntilAtOrBelowLimit()
    {
        var share = await AddShareAsync(mode: RecycleLimitMode.Custom, maxBytes: 250);
        Write(share, $"{Bin}/1.txt");
        await RunAsync();
        Advance(TimeSpan.FromHours(1));
        Write(share, $"{Bin}/2.txt");
        await RunAsync();   // 200 bytes: below the limit
        Advance(TimeSpan.FromHours(1));
        Write(share, $"{Bin}/3.txt");
        await RunAsync();   // 300 bytes: the oldest goes

        Assert.False(Exists(share, $"{Bin}/1.txt"));
        Assert.True(Exists(share, $"{Bin}/2.txt"));
        Assert.True(Exists(share, $"{Bin}/3.txt"));
    }

    [Fact]
    public async Task MaxSize_InheritUsesGlobalDefault()
    {
        _globalMax = 150;
        var share = await AddShareAsync();
        Write(share, $"{Bin}/1.txt");
        await RunAsync();
        Advance(TimeSpan.FromHours(1));
        Write(share, $"{Bin}/2.txt");
        await RunAsync();

        Assert.False(Exists(share, $"{Bin}/1.txt"));
        Assert.True(Exists(share, $"{Bin}/2.txt"));
    }

    [Fact]
    public async Task MaxSize_UnlimitedIgnoresGlobalDefault()
    {
        _globalMax = 150;
        var share = await AddShareAsync(mode: RecycleLimitMode.Unlimited);
        Write(share, $"{Bin}/1.txt");
        Write(share, $"{Bin}/2.txt");
        await RunAsync();

        Assert.True(Exists(share, $"{Bin}/1.txt"));
        Assert.True(Exists(share, $"{Bin}/2.txt"));
    }

    [Fact]
    public async Task Homes_LimitAppliesPerHomeBin()
    {
        var share = await AddShareAsync(name: "users", homes: true, mode: RecycleLimitMode.Custom, maxBytes: 150);
        var alice = Guid.NewGuid().ToString();
        var bob = Guid.NewGuid().ToString();
        Write(share, $"{alice}/{Bin}/a1.txt");
        Write(share, $"{bob}/{Bin}/b1.txt");
        await RunAsync();
        Advance(TimeSpan.FromHours(1));
        Write(share, $"{alice}/{Bin}/a2.txt");
        await RunAsync();

        Assert.False(Exists(share, $"{alice}/{Bin}/a1.txt"));
        Assert.True(Exists(share, $"{alice}/{Bin}/a2.txt"));
        Assert.True(Exists(share, $"{bob}/{Bin}/b1.txt"));
    }

    // ── folders ──

    [Fact]
    public async Task FoldersEmptiedByPurge_AreRemoved_BinRootKept()
    {
        var share = await AddShareAsync(retentionDays: 7);
        Write(share, $"{Bin}/projects/a/old.txt");
        await RunAsync();
        Advance(TimeSpan.FromDays(8));
        Write(share, $"{Bin}/projects/b/young.txt");
        await RunAsync();

        Assert.False(Exists(share, $"{Bin}/projects/a"));
        Assert.True(Exists(share, $"{Bin}/projects/b/young.txt"));
        Assert.True(Directory.Exists(Path.Combine(share.Path, Bin)));
    }

    [Fact]
    public async Task FolderDeletedWhileEmpty_IsKeptUntilItsOwnRetention()
    {
        var share = await AddShareAsync(retentionDays: 7);
        Write(share, $"{Bin}/old.txt");
        await RunAsync();
        Advance(TimeSpan.FromDays(6));
        Directory.CreateDirectory(Path.Combine(share.Path, Bin, "empty"));
        await RunAsync();
        Advance(TimeSpan.FromDays(2));
        await RunAsync();   // old.txt purged; "empty" is only 2 days in the bin

        Assert.False(Exists(share, $"{Bin}/old.txt"));
        Assert.True(Exists(share, $"{Bin}/empty"));

        Advance(TimeSpan.FromDays(6));
        await RunAsync();

        Assert.False(Exists(share, $"{Bin}/empty"));
        Assert.True(Directory.Exists(Path.Combine(share.Path, Bin)));
    }

    [Fact]
    public async Task FolderDeletedWhileEmpty_WithoutRetention_IsKept()
    {
        var share = await AddShareAsync(mode: RecycleLimitMode.Custom, maxBytes: 150);
        Directory.CreateDirectory(Path.Combine(share.Path, Bin, "empty"));
        Write(share, $"{Bin}/1.txt");
        await RunAsync();
        Advance(TimeSpan.FromHours(1));
        Write(share, $"{Bin}/2.txt");
        await RunAsync();

        Assert.False(Exists(share, $"{Bin}/1.txt"));
        Assert.True(Exists(share, $"{Bin}/empty"));
    }

    [Fact]
    public async Task Symlinks_AreRemovedNotFollowed()
    {
        var share = await AddShareAsync(retentionDays: 1);
        var outside = Path.Combine(_root, "outside");
        Directory.CreateDirectory(outside);
        File.WriteAllText(Path.Combine(outside, "keep.txt"), "x");
        Directory.CreateDirectory(Path.Combine(share.Path, Bin));
        try
        {
            Directory.CreateSymbolicLink(Path.Combine(share.Path, Bin, "link"), outside);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return;   // symlinks need privileges on Windows
        }

        await RunAsync();
        Advance(TimeSpan.FromDays(2));
        await RunAsync();

        Assert.False(Path.Exists(Path.Combine(share.Path, Bin, "link")));
        Assert.True(File.Exists(Path.Combine(outside, "keep.txt")));
        Assert.DoesNotContain(await RowsAsync(), r => r.Path.Contains("keep.txt"));
    }

    // ── purge mechanics ──

    [Fact]
    public async Task Purge_RemovesMetadataVersionsAndLogsChange()
    {
        var share = await AddShareAsync(retentionDays: 1);
        Write(share, $"{Bin}/old.txt");
        await RunAsync();
        Advance(TimeSpan.FromDays(2));
        await RunAsync();

        _acls.Verify(a => a.DeleteFileMetadataPathsAsync(share.Id, $"{Bin}/old.txt"), Times.Once);
        _versions.Verify(v => v.DeletePathAsync(share.Id, $"{Bin}/old.txt"), Times.Once);
        var change = Assert.Single(_changes);
        Assert.Equal(FileChangeType.Deleted, change.ChangeType);
        Assert.Equal(share.Id, change.ShareId);
        Assert.Equal($"{Bin}/old.txt", change.Path);
        Assert.False(change.IsDirectory);
    }

    [Fact]
    public async Task Purge_ChangedCtime_IsSkipped()
    {
        var share = await AddShareAsync();
        Write(share, $"{Bin}/raced.txt");

        var purged = await _sut.PurgeEntryAsync(share, $"{Bin}/raced.txt", isDirectory: false, expectedCtimeTicks: 42);

        Assert.False(purged);
        Assert.True(Exists(share, $"{Bin}/raced.txt"));
        Assert.Empty(_changes);
    }

    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public async Task DisabledShareOrBin_IsUntouched(bool enabled, bool recycle)
    {
        var share = await AddShareAsync(retentionDays: 1, enabled: enabled, recycle: recycle);
        Write(share, $"{Bin}/old.txt");
        await RunAsync();
        Advance(TimeSpan.FromDays(2));
        await RunAsync();

        Assert.True(Exists(share, $"{Bin}/old.txt"));
        Assert.Empty(await RowsAsync());
    }

    [Fact]
    public async Task MissingBin_IsSkipped()
    {
        await AddShareAsync(retentionDays: 1);

        await RunAsync();

        Assert.Empty(await RowsAsync());
    }

    private sealed class FakeTime(DateTimeOffset start) : TimeProvider
    {
        private DateTimeOffset _now = start;
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan by) => _now += by;
    }
}

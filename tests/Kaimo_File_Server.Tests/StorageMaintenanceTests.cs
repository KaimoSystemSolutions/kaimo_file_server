using Kaimo_File_Server.Infrastructure.Maintenance;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Kaimo_File_Server.Tests;

/// <summary>
/// Tests for the storage-maintenance sweepers (plan Phase 9). Never runs the hosted
/// service's ExecuteAsync — only the pure/unit-testable sweep methods and decisions.
/// </summary>
public sealed class StorageMaintenanceTests : IDisposable
{
    private sealed class MutableTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        private DateTimeOffset _utcNow = utcNow;
        public override DateTimeOffset GetUtcNow() => _utcNow;
        public void Advance(TimeSpan duration) => _utcNow += duration;
    }

    private readonly string _root;
    private readonly DateTimeOffset _now = new(2026, 6, 1, 12, 0, 0, TimeSpan.Zero);
    private readonly ShareTempArtifactSweeper _sweeper;

    public StorageMaintenanceTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "kaimo_maint_test_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
        _sweeper = new ShareTempArtifactSweeper(
            NullLogger<ShareTempArtifactSweeper>.Instance, new MutableTimeProvider(_now));
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_root)) Directory.Delete(_root, true); } catch { }
    }

    private static string TempArtifactName()
        => $".report.docx.kaimo-{Guid.NewGuid():N}.tmp";

    private static string StagingDirName()
        => $"folder.kaimo-moving-{Guid.NewGuid():N}";

    private string WriteFile(string relative, DateTime lastWriteUtc)
    {
        var path = Path.Combine(_root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "x");
        File.SetLastWriteTimeUtc(path, lastWriteUtc);
        return path;
    }

    // ── SweepShare ─────────────────────────────────────────────────────

    [Fact]
    public void SweepShare_TempFileOlderThanGrace_IsDeleted()
    {
        var path = WriteFile(TempArtifactName(), _now.UtcDateTime.AddHours(-25));

        var result = _sweeper.SweepShare(_root, TimeSpan.FromHours(24), CancellationToken.None);

        Assert.False(File.Exists(path));
        Assert.Equal(1, result.Deleted);
    }

    [Fact]
    public void SweepShare_TempFileWithinGrace_IsKept()
    {
        var path = WriteFile(TempArtifactName(), _now.UtcDateTime.AddHours(-1));

        var result = _sweeper.SweepShare(_root, TimeSpan.FromHours(24), CancellationToken.None);

        Assert.True(File.Exists(path));
        Assert.Equal(0, result.Deleted);
    }

    [Fact]
    public void SweepShare_RegularFileWithSimilarName_IsKept()
    {
        // Contains ".kaimo-" but is not the exact transient shape.
        var path = WriteFile("my.kaimo-notes.txt", _now.UtcDateTime.AddHours(-100));

        var result = _sweeper.SweepShare(_root, TimeSpan.FromHours(24), CancellationToken.None);

        Assert.True(File.Exists(path));
        Assert.Equal(0, result.Deleted);
    }

    [Fact]
    public void SweepShare_TempFileAtDepth_IsDeleted()
    {
        var path = WriteFile(Path.Combine("a", "b", TempArtifactName()), _now.UtcDateTime.AddHours(-30));

        var result = _sweeper.SweepShare(_root, TimeSpan.FromHours(24), CancellationToken.None);

        Assert.False(File.Exists(path));
        Assert.Equal(1, result.Deleted);
    }

    // ── SweepPool ──────────────────────────────────────────────────────

    [Fact]
    public void SweepPool_StagingDirectoryOlderThanGrace_IsDeleted()
    {
        var dir = Path.Combine(_root, StagingDirName());
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "part.bin"), "x");
        SetTreeWriteTime(dir, _now.UtcDateTime.AddHours(-200));

        var result = _sweeper.SweepPool(_root, TimeSpan.FromHours(168), CancellationToken.None);

        Assert.False(Directory.Exists(dir));
        Assert.Equal(1, result.Deleted);
    }

    [Fact]
    public void SweepPool_StagingDirectoryWithRecentChildWrite_IsKept()
    {
        var dir = Path.Combine(_root, StagingDirName());
        Directory.CreateDirectory(dir);
        var child = Path.Combine(dir, "part.bin");
        File.WriteAllText(child, "x");
        File.SetLastWriteTimeUtc(child, _now.UtcDateTime.AddMinutes(-1)); // an in-flight copy
        // Set the directory time LAST — creating the child above bumped it to real-now.
        Directory.SetLastWriteTimeUtc(dir, _now.UtcDateTime.AddHours(-200));

        var result = _sweeper.SweepPool(_root, TimeSpan.FromHours(168), CancellationToken.None);

        Assert.True(Directory.Exists(dir));
        Assert.Equal(0, result.Deleted);
    }

    [Fact]
    public void SweepPool_RealShareDirectory_IsNeverTouched()
    {
        var share = Path.Combine(_root, "test"); // a normal share directory
        Directory.CreateDirectory(share);
        File.WriteAllText(Path.Combine(share, "doc.txt"), "x");
        SetTreeWriteTime(share, _now.UtcDateTime.AddHours(-1000));

        var result = _sweeper.SweepPool(_root, TimeSpan.FromHours(168), CancellationToken.None);

        Assert.True(Directory.Exists(share));
        Assert.Equal(0, result.Examined);
        Assert.Equal(0, result.Deleted);
    }

    // ── EffectiveLastWriteUtc ──────────────────────────────────────────

    [Fact]
    public void EffectiveLastWriteUtc_UsesNewestImmediateChild()
    {
        var dir = Path.Combine(_root, "d");
        Directory.CreateDirectory(dir);
        var child = Path.Combine(dir, "c.bin");
        File.WriteAllText(child, "x");
        var childTime = _now.UtcDateTime.AddHours(-2);
        File.SetLastWriteTimeUtc(child, childTime);
        // Set the directory time LAST (older than the child) — creating the child bumped it.
        Directory.SetLastWriteTimeUtc(dir, _now.UtcDateTime.AddHours(-50));

        var effective = ShareTempArtifactSweeper.EffectiveLastWriteUtc(new DirectoryInfo(dir));

        Assert.Equal(childTime, effective);
    }

    // ── IsStepDue ──────────────────────────────────────────────────────

    [Fact]
    public void IsStepDue_FirstRun_IsTrue()
        => Assert.True(StorageMaintenanceService.IsStepDue(_now, null, TimeSpan.FromHours(1)));

    [Fact]
    public void IsStepDue_WithinInterval_IsFalse()
        => Assert.False(StorageMaintenanceService.IsStepDue(_now, _now.AddMinutes(-30), TimeSpan.FromHours(1)));

    [Fact]
    public void IsStepDue_AfterInterval_IsTrue()
        => Assert.True(StorageMaintenanceService.IsStepDue(_now, _now.AddHours(-2), TimeSpan.FromHours(1)));

    private static void SetTreeWriteTime(string dir, DateTime utc)
    {
        foreach (var file in Directory.GetFiles(dir, "*", SearchOption.AllDirectories))
            File.SetLastWriteTimeUtc(file, utc);
        foreach (var sub in Directory.GetDirectories(dir, "*", SearchOption.AllDirectories))
            Directory.SetLastWriteTimeUtc(sub, utc);
        Directory.SetLastWriteTimeUtc(dir, utc);
    }
}

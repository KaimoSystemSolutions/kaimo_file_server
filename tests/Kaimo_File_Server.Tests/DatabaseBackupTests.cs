using Kaimo_File_Server.Infrastructure.Backup;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Kaimo_File_Server.Tests;

public sealed class DatabaseBackupTests
{
    // ── BackupFileNaming ──────────────────────────────────────────────

    [Theory]
    [InlineData(BackupTrigger.Scheduled)]
    [InlineData(BackupTrigger.PreMigration)]
    [InlineData(BackupTrigger.Manual)]
    public void FileName_RoundTrips_TriggerAndTimestamp(BackupTrigger trigger)
    {
        var created = new DateTimeOffset(2026, 8, 20, 3, 15, 42, TimeSpan.Zero);
        var name = BackupFileNaming.Build(created, trigger);

        var parsed = BackupFileNaming.TryParse(name);

        Assert.NotNull(parsed);
        Assert.Equal(trigger, parsed!.Value.Trigger);
        // AssumeLocal reparses in the machine offset; compare the wall-clock only.
        Assert.Equal(created.DateTime, parsed.Value.CreatedAtLocal.DateTime);
    }

    [Theory]
    [InlineData("kaimo_20260820-031542_manual.dump.done")] // marker file
    [InlineData("random.txt")]
    [InlineData("kaimo_notatimestamp_manual.dump")]
    [InlineData("kaimo_20260820-031542.dump")]             // no trigger segment
    public void TryParse_Rejects_NonBackupNames(string name)
        => Assert.Null(BackupFileNaming.TryParse(name));

    [Fact]
    public void TryParse_UnknownTrigger_DefaultsToManual()
    {
        var parsed = BackupFileNaming.TryParse("kaimo_20260820-031542_bogus.dump");
        Assert.NotNull(parsed);
        Assert.Equal(BackupTrigger.Manual, parsed!.Value.Trigger);
    }

    // ── Scheduler window / due logic ──────────────────────────────────

    [Theory]
    [InlineData(2, 4, 3, true)]    // inside a normal window
    [InlineData(2, 4, 1, false)]   // before it
    [InlineData(2, 4, 5, false)]   // after it
    [InlineData(2, 4, 2, true)]    // on the start boundary
    [InlineData(2, 4, 4, true)]    // on the end boundary
    [InlineData(23, 2, 23, true)]  // wrapping window, before midnight
    [InlineData(23, 2, 1, true)]   // wrapping window, after midnight
    [InlineData(23, 2, 12, false)] // wrapping window, midday outside
    public void IsWithinWindow_Handles_NormalAndWrapping(int startH, int endH, int nowH, bool expected)
    {
        var result = DatabaseBackupSchedulerService.IsWithinWindow(
            new TimeOnly(startH, 0), new TimeOnly(endH, 0), new TimeOnly(nowH, 0));
        Assert.Equal(expected, result);
    }

    [Fact]
    public void IsDue_False_WhenDisabled()
    {
        var settings = new BackupSettings { Enabled = false, WindowStart = new(2, 0), WindowEnd = new(4, 0) };
        var now = LocalAt(3);
        Assert.False(DatabaseBackupSchedulerService.IsDue(settings, now, lastScheduledDate: null));
    }

    [Fact]
    public void IsDue_True_WhenInWindowAndNoBackupToday()
    {
        var settings = new BackupSettings { Enabled = true, WindowStart = new(2, 0), WindowEnd = new(4, 0) };
        var now = LocalAt(3);
        Assert.True(DatabaseBackupSchedulerService.IsDue(settings, now, lastScheduledDate: null));
    }

    [Fact]
    public void IsDue_False_WhenAlreadyRanToday()
    {
        var settings = new BackupSettings { Enabled = true, WindowStart = new(2, 0), WindowEnd = new(4, 0) };
        var now = LocalAt(3);
        var today = DateOnly.FromDateTime(now.DateTime);
        Assert.False(DatabaseBackupSchedulerService.IsDue(settings, now, today));
    }

    [Fact]
    public void IsDue_False_OutsideWindow()
    {
        var settings = new BackupSettings { Enabled = true, WindowStart = new(2, 0), WindowEnd = new(4, 0) };
        var now = LocalAt(10);
        Assert.False(DatabaseBackupSchedulerService.IsDue(settings, now, lastScheduledDate: null));
    }

    // ── BackupSettings.Normalize ──────────────────────────────────────

    [Fact]
    public void Normalize_Clamps_Retention()
    {
        var settings = new BackupSettings { RetentionCount = 0, RetentionDays = 100000 };
        settings.Normalize();
        Assert.Equal(BackupSettings.MinRetentionCount, settings.RetentionCount);
        Assert.Equal(BackupSettings.MaxRetentionDays, settings.RetentionDays);
    }

    // ── Retention pruning ─────────────────────────────────────────────

    [Fact]
    public async Task Prune_Keeps_RecentScheduled_Deletes_ExcessAndOld()
    {
        using var temp = new TempDir();
        var now = DateTimeOffset.Now;

        // 3 recent scheduled backups + 1 that is 40 days old.
        var recent0 = WriteBackup(temp.Path, now.AddMinutes(-1), BackupTrigger.Scheduled);
        var recent1 = WriteBackup(temp.Path, now.AddDays(-1), BackupTrigger.Scheduled);
        var recent2 = WriteBackup(temp.Path, now.AddDays(-2), BackupTrigger.Scheduled);
        var old = WriteBackup(temp.Path, now.AddDays(-40), BackupTrigger.Scheduled);
        // A manual backup that must never be auto-deleted, even when very old.
        var manualOld = WriteBackup(temp.Path, now.AddDays(-400), BackupTrigger.Manual);

        var service = CreateService(temp.Path);
        // Keep last 2 scheduled, delete scheduled older than 30 days.
        await service.PruneAsync(new BackupSettings { RetentionCount = 2, RetentionDays = 30 });

        Assert.True(File.Exists(recent0));   // newest kept
        Assert.True(File.Exists(recent1));   // 2nd newest kept
        Assert.False(File.Exists(recent2));  // beyond RetentionCount → deleted
        Assert.False(File.Exists(old));      // older than RetentionDays → deleted
        Assert.True(File.Exists(manualOld)); // manual never auto-deleted
    }

    // ── Pruning self-heal (plan Phase 6) ──────────────────────────────

    [Fact]
    public void ShouldPrune_FirstTick_ReturnsTrue()
        => Assert.True(DatabaseBackupSchedulerService.ShouldPrune(DateTimeOffset.Now, lastPruneLocal: null));

    [Fact]
    public void ShouldPrune_WithinInterval_ReturnsFalse()
    {
        var now = DateTimeOffset.Now;
        var lastPrune = now - TimeSpan.FromMinutes(30); // < 1h PruneInterval
        Assert.False(DatabaseBackupSchedulerService.ShouldPrune(now, lastPrune));
    }

    [Fact]
    public void ShouldPrune_AfterInterval_ReturnsTrue()
    {
        var now = DateTimeOffset.Now;
        var lastPrune = now - DatabaseBackupSchedulerService.PruneInterval;
        Assert.True(DatabaseBackupSchedulerService.ShouldPrune(now, lastPrune));
    }

    [Fact]
    public async Task PruneAsync_WhenBackupsDisabled_KeepsScheduledBackups()
    {
        using var temp = new TempDir();
        var now = DateTimeOffset.Now;

        // Scheduled backups that WOULD be pruned by count/age if scheduled pruning ran.
        var newest = WriteBackup(temp.Path, now.AddMinutes(-1), BackupTrigger.Scheduled);
        var excess = WriteBackup(temp.Path, now.AddDays(-2), BackupTrigger.Scheduled);
        var old = WriteBackup(temp.Path, now.AddDays(-40), BackupTrigger.Scheduled);

        var service = CreateService(temp.Path);
        await service.PruneAsync(
            new BackupSettings { RetentionCount = 1, RetentionDays = 30 }, pruneScheduled: false);

        // Disabling scheduled backups must not delete the ones already taken.
        Assert.True(File.Exists(newest));
        Assert.True(File.Exists(excess));
        Assert.True(File.Exists(old));
    }

    [Fact]
    public async Task PruneAsync_WhenBackupsDisabled_StillRemovesExpiredPreMigrationBackups()
    {
        using var temp = new TempDir();
        var now = DateTimeOffset.Now;

        // Pre-migration backups have their own fixed 90-day retention and are the actual
        // unbounded source, so they must still be pruned even when scheduled pruning is off.
        var expired = WriteBackup(temp.Path, now.AddDays(-100), BackupTrigger.PreMigration);
        var recent = WriteBackup(temp.Path, now.AddDays(-10), BackupTrigger.PreMigration);

        var service = CreateService(temp.Path);
        await service.PruneAsync(
            new BackupSettings { RetentionCount = 5, RetentionDays = 30 }, pruneScheduled: false);

        Assert.False(File.Exists(expired));
        Assert.True(File.Exists(recent));
    }

    [Fact]
    public async Task PruneAsync_ManualBackups_AreNeverDeleted()
    {
        using var temp = new TempDir();
        var now = DateTimeOffset.Now;
        var manualOld = WriteBackup(temp.Path, now.AddDays(-400), BackupTrigger.Manual);

        var service = CreateService(temp.Path);
        await service.PruneAsync(new BackupSettings { RetentionCount = 1, RetentionDays = 30 });

        Assert.True(File.Exists(manualOld));
    }

    [Fact]
    public void ResolveBackupPath_Rejects_Traversal()
    {
        using var temp = new TempDir();
        var service = CreateService(temp.Path);
        Assert.Null(service.ResolveBackupPath("../secret.dump"));
        Assert.Null(service.ResolveBackupPath("kaimo_20260820-031542_manual.dump")); // does not exist
    }

    // ── Helpers ───────────────────────────────────────────────────────

    private static DateTimeOffset LocalAt(int hour)
    {
        var d = new DateTime(2026, 8, 20, hour, 0, 0, DateTimeKind.Local);
        return new DateTimeOffset(d);
    }

    private static string WriteBackup(string dir, DateTimeOffset created, BackupTrigger trigger)
    {
        var path = Path.Combine(dir, BackupFileNaming.Build(created, trigger));
        File.WriteAllText(path, "dummy");
        return path;
    }

    private static DatabaseBackupService CreateService(string rootPath)
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Backup:RootPath"] = rootPath,
                ["ConnectionStrings:Default"] =
                    "Host=db;Database=kaimo;Username=u;Password=p",
            })
            .Build();

        return new DatabaseBackupService(
            config,
            new StubProcessRunner(),
            TimeProvider.System,
            NullLogger<DatabaseBackupService>.Instance);
    }

    private sealed class StubProcessRunner : IProcessRunner
    {
        public Task<ProcessResult> RunAsync(
            string fileName,
            IReadOnlyList<string> arguments,
            IReadOnlyDictionary<string, string>? environment = null,
            CancellationToken cancellationToken = default)
            => Task.FromResult(new ProcessResult(0, "", ""));
    }

    private sealed class TempDir : IDisposable
    {
        public string Path { get; } =
            System.IO.Path.Combine(System.IO.Path.GetTempPath(), "kaimo-backup-tests-" + Guid.NewGuid().ToString("N"));

        public TempDir() => Directory.CreateDirectory(Path);

        public void Dispose()
        {
            try { Directory.Delete(Path, recursive: true); } catch { /* best effort */ }
        }
    }
}

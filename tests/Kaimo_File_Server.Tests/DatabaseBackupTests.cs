using Kaimo_File_Server.Infrastructure;
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

    [Fact]
    public void ResolveBackupPath_Rejects_Traversal()
    {
        using var temp = new TempDir();
        var service = CreateService(temp.Path);
        Assert.Null(service.ResolveBackupPath("../secret.dump"));
        Assert.Null(service.ResolveBackupPath("kaimo_20260820-031542_manual.dump")); // does not exist
    }

    [Fact]
    public void DeleteBackup_Removes_ValidFile_And_Rejects_Traversal()
    {
        using var temp = new TempDir();
        var service = CreateService(temp.Path);
        var outside = Path.Combine(Path.GetDirectoryName(temp.Path)!, "kaimo_20260820-031542_manual.dump");
        var real = WriteBackup(temp.Path, DateTimeOffset.Now, BackupTrigger.Manual);

        Assert.False(service.DeleteBackup("../" + Path.GetFileName(outside)));
        Assert.True(service.DeleteBackup(Path.GetFileName(real)));
        Assert.False(File.Exists(real));
        Assert.False(service.DeleteBackup(Path.GetFileName(real))); // already gone
    }

    // ── Startup restore target resolution ─────────────────────────────

    [Fact]
    public void ResolveRestoreTarget_Accepts_BareName_HostPath_And_MissingExtension()
    {
        using var temp = new TempDir();
        var real = WriteBackup(temp.Path, DateTimeOffset.Now, BackupTrigger.Scheduled);
        var name = Path.GetFileName(real);
        var nameNoExt = name[..^BackupFileNaming.Extension.Length];

        // Bare file name inside the backup folder.
        Assert.Equal(real, ServiceCollectionExtensions.ResolveRestoreTarget(name, temp.Path));
        // File name without the ".dump" extension.
        Assert.Equal(real, ServiceCollectionExtensions.ResolveRestoreTarget(nameNoExt, temp.Path));
        // A stale/wrong host-path prefix — only the file name is used.
        Assert.Equal(real, ServiceCollectionExtensions.ResolveRestoreTarget("/data/dataset00/backups/" + nameNoExt, temp.Path));
        // The correct absolute path is honoured as-is.
        Assert.Equal(real, ServiceCollectionExtensions.ResolveRestoreTarget(real, temp.Path));
    }

    [Fact]
    public void ResolveRestoreTarget_ReturnsNull_WhenNothingMatches()
    {
        using var temp = new TempDir();
        Assert.Null(ServiceCollectionExtensions.ResolveRestoreTarget(
            "kaimo_20260820-031542_manual.dump", temp.Path));
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

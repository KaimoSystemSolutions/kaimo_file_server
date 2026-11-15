using Xunit;
using Kaimo_File_Server.Core.Domain.Backup;
using Kaimo_File_Server.Infrastructure.Backup.Restic;
using Microsoft.EntityFrameworkCore;

namespace Kaimo_File_Server.Infrastructure.Tests.Backup;

public partial class ResticBackupDatabaseTests
{
    [Fact]
    public async Task PruneRuns_RemovesOnlyFinishedRunsOlderThanTheRetention()
    {
        var now = new DateTime(2026, 11, 15, 12, 0, 0, DateTimeKind.Utc);
        var old = now.AddDays(-BackupSchedulerService.RunRetentionDays - 1);
        var recent = now.AddDays(-BackupSchedulerService.RunRetentionDays + 1);
        var (_, _, _, _, repo) = await SeedAsync(releaseToParent: false);
        BackupRun Run(DateTime queued, BackupRunStatus status)
            => new() { RepositoryId = repo.Id, Type = BackupRunType.Backup, QueuedAtUtc = queued, Status = status };
        var oldSucceeded = Run(old, BackupRunStatus.Succeeded);
        var oldFailed = Run(old, BackupRunStatus.Failed);
        var oldRunning = Run(old, BackupRunStatus.Running);
        var oldQueued = Run(old, BackupRunStatus.Queued);
        var recentSucceeded = Run(recent, BackupRunStatus.Succeeded);
        await using (var db = NewContext())
        {
            db.BackupRuns.AddRange(oldSucceeded, oldFailed, oldRunning, oldQueued, recentSucceeded);
            await db.SaveChangesAsync();
        }

        int removed;
        await using (var db = NewContext())
            removed = await BackupSchedulerService.PruneRunsAsync(db, now, CancellationToken.None);

        Assert.Equal(2, removed);
        await using var check = NewContext();
        var left = await check.BackupRuns.Select(r => r.Id).ToListAsync();
        Assert.Equal(
            new[] { oldRunning.Id, oldQueued.Id, recentSucceeded.Id }.Order(),
            left.Order());
    }
}

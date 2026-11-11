using Kaimo_File_Server.Core.Domain.Backup;
using Kaimo_File_Server.Infrastructure.Backup.Restic;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Kaimo_File_Server.Infrastructure.Tests.Backup;

/// <summary>
/// Restoring a share backup point into the server restore folder: only allowed when its share no
/// longer exists on this server (deleted, or the repository comes from another installation).
/// </summary>
public sealed partial class ResticRestoreDatabaseTests
{
    private async Task<(BackupRun Run, BackupRestoreOutcome? Result)> RunServerRestoreAsync(bool shareGone)
    {
        var fixture = await MountRestoreFixtureAsync();
        await using (var db = NewContext())
        {
            var snapshot = await db.BackupSnapshots.SingleAsync(s => s.SnapshotId == "snap");
            if (shareGone)
                snapshot.ShareId = Guid.NewGuid();
            var run = await db.BackupRuns.SingleAsync(r => r.Id == fixture.RunId);
            run.DetailsJson = new BackupRestoreDetails(new BackupRestoreRequest(run.RepositoryId, "snap", Guid.Empty, string.Empty, [],
                BackupRestoreMode.ServerFolder, BackupRestoreConflict.Skip, false)).Serialize();
            await db.SaveChangesAsync();
        }
        return await ExecuteAsync(fixture);
    }

    [Fact]
    public async Task ServerRestore_TakesASharePointWhoseShareIsGone()
    {
        var (run, result) = await RunServerRestoreAsync(shareGone: true);

        Assert.Equal(BackupRunStatus.Succeeded, run.Status);
        Assert.True(File.Exists(Path.Combine(result!.DestinationPath!, "Team", "new.txt")));
        Assert.Contains("/data/share", result.SourceShareName);
    }

    [Fact]
    public async Task ServerRestore_RefusesASharePointWhoseShareExists()
    {
        var (run, _) = await RunServerRestoreAsync(shareGone: false);

        Assert.Equal((BackupRunStatus.Failed, "request_invalid"), (run.Status, run.ErrorCode));
    }
}

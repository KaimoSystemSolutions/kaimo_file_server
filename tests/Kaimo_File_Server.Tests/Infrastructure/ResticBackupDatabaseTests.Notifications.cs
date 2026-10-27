using Xunit;
using System.Text.Json;
using Kaimo_File_Server.Core.Domain.Backup;
using Kaimo_File_Server.Core.Services.Notifications;
using Microsoft.EntityFrameworkCore;

namespace Kaimo_File_Server.Tests;

public partial class ResticBackupDatabaseTests
{
    // ══════════════════════════════════════════
    //  Notifications of backup job runs
    // ══════════════════════════════════════════

    private static ScriptedRunner BackupRunner(params string[] backupOutput) => new(command => command switch
    {
        "backup" => (0, backupOutput),
        "snapshots" => (0, ["[]"]),
        _ => (0, []),
    });

    private async Task<(MaintenanceFixture Fixture, Guid JobId, Guid RunId)> ShareRunAsync(ScriptedRunner runner, BackupRunTrigger trigger = BackupRunTrigger.Manual)
    {
        var fixture = await MaintenanceFixtureAsync(runner, appendOnly: true);
        var shareDir = Path.Combine(fixture.Root, "share");
        Directory.CreateDirectory(shareDir);
        Directory.CreateDirectory(Path.Combine(fixture.Root, "repo"));
        var (jobId, runId) = await AddRunAsync(fixture, BackupContentLevel.Files, includeVersions: false, shareDir: shareDir);
        await using var db = NewContext();
        (await db.BackupRuns.SingleAsync(r => r.Id == runId)).Trigger = trigger;
        await db.SaveChangesAsync();
        return (fixture, jobId, runId);
    }

    private static Dictionary<string, string> Payload(Core.Domain.Notifications.NotificationEvent e)
        => JsonSerializer.Deserialize<Dictionary<string, string>>(e.PayloadJson)!;

    [Fact]
    public async Task Backup_Succeeded_PublishesSucceededEventWithRunSummary()
    {
        var runner = BackupRunner("{\"message_type\":\"summary\",\"snapshot_id\":\"s1\",\"files_new\":12,\"files_changed\":3,\"data_added\":2048}");
        var (fixture, jobId, runId) = await ShareRunAsync(runner, BackupRunTrigger.Scheduled);

        await fixture.Jobs.ExecuteAsync(runId, (_, _) => { }, CancellationToken.None);

        var e = Assert.Single(fixture.Published, e => e.Type == NotificationEventCatalog.FileBackupSucceeded);
        var payload = Payload(e);
        Assert.Equal("Keep all", payload["job.name"]);
        Assert.Equal("NAS", payload["repository.name"]);
        Assert.Equal("Scheduled", payload["backup.trigger"]);
        Assert.Equal("12", payload["filesNew"]);
        Assert.Equal("3", payload["filesChanged"]);
        Assert.Equal("2 KB", payload["dataAdded"]);
        Assert.Matches(@"^\d{2}:\d{2}:\d{2}$", payload["duration"]);
        // Keyed per job so a throttle configured on the rule takes effect.
        Assert.Equal("filebackup-succeeded:" + jobId.ToString("N"), e.DedupKey);
        Assert.DoesNotContain(fixture.Published, e => e.Type is NotificationEventCatalog.FileBackupFailed or NotificationEventCatalog.FileBackupWarnings);
    }

    [Fact]
    public async Task Backup_SucceededWithWarnings_PublishesOnlyTheWarningsEvent()
    {
        var runner = BackupRunner(
            "{\"message_type\":\"error\",\"error\":{\"message\":\"permission denied\"},\"item\":\"/share/locked.txt\"}",
            "{\"message_type\":\"summary\",\"snapshot_id\":\"s1\",\"files_new\":1}");
        var (fixture, _, runId) = await ShareRunAsync(runner);

        await fixture.Jobs.ExecuteAsync(runId, (_, _) => { }, CancellationToken.None);

        Assert.Contains(fixture.Published, e => e.Type == NotificationEventCatalog.FileBackupWarnings);
        Assert.DoesNotContain(fixture.Published, e => e.Type == NotificationEventCatalog.FileBackupSucceeded);
    }

    [Fact]
    public async Task Backup_Failed_DoesNotPublishSucceededEvent()
    {
        var runner = new ScriptedRunner(command => command switch
        {
            "backup" => (1, []),
            "snapshots" => (0, ["[]"]),
            _ => (0, []),
        });
        var (fixture, _, runId) = await ShareRunAsync(runner);

        await fixture.Jobs.ExecuteAsync(runId, (_, _) => { }, CancellationToken.None);

        Assert.Contains(fixture.Published, e => e.Type == NotificationEventCatalog.FileBackupFailed);
        Assert.DoesNotContain(fixture.Published, e => e.Type == NotificationEventCatalog.FileBackupSucceeded);
    }

    [Fact]
    public void FileBackupSucceeded_IsAFileBackupEventForTheJobCreatorAndJobAdmins()
    {
        var definition = NotificationEventCatalog.Find(NotificationEventCatalog.FileBackupSucceeded);

        Assert.NotNull(definition);
        Assert.Equal("backup.restic.succeeded", definition.Type);
        Assert.Equal("filebackup", definition.Category);
        Assert.Equal([NotificationContextRoles.JobCreator], definition.ContextRoles);
        Assert.Equal(["job.name", "repository.name", "backup.trigger", "filesNew", "filesChanged", "dataAdded", "duration"],
            definition.Placeholders.Select(p => p.Name));
        Assert.Contains(definition.DefaultRecipients, r => r.Kind == Core.Domain.Notifications.RecipientKind.PermissionHolder && r.Value == "ManageBackupJobs");
        Assert.Equal(0, definition.DefaultThrottleMinutes);
    }

    [Fact]
    public void FileBackupSucceeded_Builder_CarriesTheJobCreatorRole()
    {
        var jobId = Guid.NewGuid();
        var creator = Guid.NewGuid();

        var e = NotificationEvents.FileBackupSucceeded(jobId, "Nightly", "NAS", creator, "Manual", 5, 0, 1536, TimeSpan.FromSeconds(3725));

        Assert.Equal(NotificationEventCatalog.FileBackupSucceeded, e.Type);
        var roles = JsonSerializer.Deserialize<Dictionary<string, Guid>>(e.SubjectUserIdsJson)!;
        Assert.Equal(creator, roles[NotificationContextRoles.JobCreator]);
        var payload = Payload(e);
        Assert.Equal("1.5 KB", payload["dataAdded"]);
        Assert.Equal("01:02:05", payload["duration"]);
        Assert.Equal("filebackup-succeeded:" + jobId.ToString("N"), e.DedupKey);
    }
}

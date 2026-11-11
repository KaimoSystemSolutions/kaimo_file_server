using System.Text.Json;
using Kaimo_File_Server.Infrastructure.Backup.Restic;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Kaimo_File_Server.Infrastructure.Tests.Backup;

public partial class ResticBackupDatabaseTests
{
    private sealed record SourcesDto(List<BackupSourceOutcome> Sources);

    [Fact]
    public async Task Backup_RecordsNewAndChangedFilesPerSource()
    {
        var runner = BackupRunner("{\"message_type\":\"summary\",\"snapshot_id\":\"s1\",\"files_new\":12,\"files_changed\":3,\"files_unmodified\":40}");
        var (fixture, _, runId) = await ShareRunAsync(runner);

        await fixture.Jobs.ExecuteAsync(runId, (_, _) => { }, CancellationToken.None);

        await using var db = NewContext();
        var run = await db.BackupRuns.SingleAsync(r => r.Id == runId);
        var source = Assert.Single(JsonSerializer.Deserialize<SourcesDto>(run.DetailsJson!, new JsonSerializerOptions(JsonSerializerDefaults.Web))!.Sources);
        Assert.Equal(("s1", 12L, 3L), (source.SnapshotId, source.FilesNew, source.FilesChanged));
    }

    [Fact]
    public void SourceOutcome_FromBeforeTheCounts_StillReads()
    {
        const string json = """{"sources":[{"shareId":null,"name":"docs","snapshotId":"s1","errorCode":null,"warningCount":0,"warnings":[]}]}""";

        var source = Assert.Single(JsonSerializer.Deserialize<SourcesDto>(json, new JsonSerializerOptions(JsonSerializerDefaults.Web))!.Sources);

        Assert.Equal((0L, 0L), (source.FilesNew, source.FilesChanged));
    }
}

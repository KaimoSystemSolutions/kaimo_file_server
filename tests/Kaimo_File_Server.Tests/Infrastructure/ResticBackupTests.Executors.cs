using Xunit;
using Kaimo_File_Server.Core.Domain;
using Kaimo_File_Server.Core.Domain.Backup;
using Kaimo_File_Server.Core.Helpers;
using Kaimo_File_Server.Infrastructure.Backup;
using Kaimo_File_Server.Infrastructure.Backup.Restic;
using Kaimo_File_Server.Infrastructure.ExternalStorage;
using Microsoft.Extensions.Configuration;

namespace Kaimo_File_Server.Tests;

public partial class ResticBackupTests
{
    // ─────────────── Executor rules ───────────────

    [Fact]
    public void ShareExcludes_AreAnchoredAtTheShareRoot()
    {
        var share = new ShareDefinition("projects", "/data/storage/pool01/projects/");
        Assert.Equal(
            ["/data/storage/pool01/projects/.kaimo-*", "/data/storage/pool01/projects/" + ShareEntryPolicy.RecycleBinName],
            BackupJobExecutor.BuildShareExcludes(share, includeRecycleBin: false));
        Assert.Equal(["/data/storage/pool01/projects/.kaimo-*"], BackupJobExecutor.BuildShareExcludes(share, includeRecycleBin: true));

        var homes = new ShareDefinition("users", "/data/storage/pool01/users") { IsUserHomes = true };
        Assert.Contains("/data/storage/pool01/users/*/.RECYCLE_BIN", BackupJobExecutor.BuildShareExcludes(homes, false));
        Assert.Contains("/data/storage/pool01/users/*/.kaimo-*", BackupJobExecutor.BuildShareExcludes(homes, false));
    }

    [Fact]
    public void BuildPoolExcludes_ExcludesInternalEntries_AndReincludesVersionsOnlyWhenWanted()
    {
        var homes = new ShareDefinition("users", "/data/storage/pool01/users") { IsUserHomes = true };

        var excludes = BackupJobExecutor.BuildPoolExcludes("/data/storage/pool01/", [homes], includeRecycleBin: false, includeVersions: false);
        Assert.Equal("/data/storage/pool01/.kaimo-*", excludes[0]);
        Assert.Contains("/data/storage/pool01/*/.kaimo-*", excludes);
        Assert.Contains("/data/storage/pool01/users/*/.RECYCLE_BIN", excludes);
        Assert.Equal(excludes.Count, excludes.Distinct().Count());
        Assert.DoesNotContain(excludes, e => e.StartsWith('!'));

        // restic: the negated pattern must come last to win over ".kaimo-*".
        excludes = BackupJobExecutor.BuildPoolExcludes("/data/storage/pool01", [homes], includeRecycleBin: true, includeVersions: true);
        Assert.Equal("!/data/storage/pool01/.kaimo-versions", excludes[^1]);
        Assert.DoesNotContain(excludes, e => e.EndsWith(ShareEntryPolicy.RecycleBinName));
    }

    [Fact]
    public void IncludesVersions_OnlyWithADatabaseLevel()
    {
        Assert.False(BackupJobExecutor.IncludesVersions(new BackupJob { IncludeVersions = true, ContentLevel = BackupContentLevel.Files }));
        Assert.True(BackupJobExecutor.IncludesVersions(new BackupJob { IncludeVersions = true, ContentLevel = BackupContentLevel.FilesAndDatabase }));
        Assert.False(BackupJobExecutor.IncludesVersions(new BackupJob { ContentLevel = BackupContentLevel.Full }));
    }

    [Fact]
    public void ApplySnapshotInfo_RecognizesPoolAndSystemBackupPoints()
    {
        var pool = new BackupSnapshot();
        BackupJobExecutor.ApplySnapshotInfo(pool, new ResticSnapshotInfo("p", DateTime.UtcNow, ["/data/storage/pool01"],
            ["kaimo", "level:db", "pool:pool01"], "kaimo", 1, 1));
        Assert.Equal((BackupSnapshotKind.Pool, "pool01", BackupContentLevel.FilesAndDatabase), (pool.Kind, pool.PoolPath, pool.ContentLevel));

        var system = new BackupSnapshot();
        BackupJobExecutor.ApplySnapshotInfo(system, new ResticSnapshotInfo("s", DateTime.UtcNow, ["/x"], ["kaimo", "level:full", "kind:system"], "kaimo", 1, 1));
        Assert.Equal((BackupSnapshotKind.System, BackupContentLevel.Full), (system.Kind, system.ContentLevel));
        Assert.Equal("pool_01", BackupJobExecutor.PoolTag("/data/storage/pool,01/"));
    }

    [Fact]
    public void GuardSource_RefusesMissingOrSuddenlyEmptySources()
    {
        var dir = Directory.CreateTempSubdirectory("kaimo-guard-").FullName;
        try
        {
            Assert.Equal("source_unavailable", Assert.Throws<ResticException>(() =>
                BackupJobExecutor.GuardSource(Path.Combine(dir, "missing"), hadContentBefore: false)).Code);
            Assert.Equal("source_empty", Assert.Throws<ResticException>(() =>
                BackupJobExecutor.GuardSource(dir, hadContentBefore: true)).Code);
            BackupJobExecutor.GuardSource(dir, hadContentBefore: false); // a new, empty share is fine
            Directory.CreateDirectory(Path.Combine(dir, ".kaimo-close-captures"));
            Assert.Equal("source_empty", Assert.Throws<ResticException>(() =>
                BackupJobExecutor.GuardSource(dir, hadContentBefore: true)).Code);
            File.WriteAllText(Path.Combine(dir, "a.txt"), "x");
            BackupJobExecutor.GuardSource(dir, hadContentBefore: true);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void SnapshotInfo_DerivesKindJobAndShareFromTags()
    {
        var job = Guid.NewGuid();
        var share = Guid.NewGuid();
        var row = new BackupSnapshot();
        BackupJobExecutor.ApplySnapshotInfo(row, new ResticSnapshotInfo("s1", DateTime.UtcNow, ["/p"],
            ["kaimo", "job:" + job.ToString("N"), "level:files", "share:" + share.ToString("N")], "kaimo", 5, 50));
        Assert.Equal(BackupSnapshotKind.Share, row.Kind);
        Assert.Equal(job, row.JobId);
        Assert.Equal(share, row.ShareId);
        Assert.Equal(BackupContentLevel.Files, row.ContentLevel);

        var foreign = new BackupSnapshot();
        BackupJobExecutor.ApplySnapshotInfo(foreign, new ResticSnapshotInfo("s2", DateTime.UtcNow, ["/x"],
            ["share:" + share.ToString("N")], "laptop", 1, 1));
        Assert.Equal(BackupSnapshotKind.Foreign, foreign.Kind);
        Assert.Null(foreign.ShareId);
    }

    // ─────────────── Release & visibility ───────────────

    [Fact]
    public void Release_IsInheritedBySubDepartments()
    {
        var parent = Guid.NewGuid();
        var child = Guid.NewGuid();
        var other = Guid.NewGuid();
        var parents = new Dictionary<Guid, Guid?> { [parent] = null, [child] = parent, [other] = null };

        Assert.True(BackupCatalogService.IsReleasedTo(new HashSet<Guid> { parent }, child, parents));
        Assert.True(BackupCatalogService.IsReleasedTo(new HashSet<Guid> { child }, child, parents));
        Assert.False(BackupCatalogService.IsReleasedTo(new HashSet<Guid> { child }, parent, parents));
        Assert.False(BackupCatalogService.IsReleasedTo(new HashSet<Guid> { parent }, other, parents));
        Assert.True(BackupCatalogService.IsReleasedTo(new HashSet<Guid> { WellKnownGUIDs.DEPARTMENT_GLOBAL }, other, parents));
    }

    [Fact]
    public void ScopedAdmin_SeesOnlyJobsWhoseSourcesAreAllInScope()
    {
        var mine = Guid.NewGuid();
        var foreign = Guid.NewGuid();
        var access = new BackupAccess(false, true, false, new HashSet<Guid> { mine });
        BackupJob Job(params Guid[] shares) => new() { Sources = shares.Select(s => new BackupJobSource { ShareId = s }).ToList() };

        Assert.True(BackupCatalogService.IsJobVisible(access, Job(mine)));
        Assert.False(BackupCatalogService.IsJobVisible(access, Job(mine, foreign)));
        Assert.False(BackupCatalogService.IsJobVisible(access, Job()));
        Assert.True(BackupCatalogService.IsJobVisible(access with { JobsUnrestricted = true }, Job(mine, foreign)));
    }
}

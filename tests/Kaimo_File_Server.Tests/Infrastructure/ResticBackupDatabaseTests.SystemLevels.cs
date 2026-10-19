using Xunit;
using System.Text.Json;
using Kaimo_File_Server.Core.Domain;
using Kaimo_File_Server.Core.Domain.Backup;
using Kaimo_File_Server.Core.Domain.Department;
using Kaimo_File_Server.Core.Domain.Identity;
using Kaimo_File_Server.Core.Security;
using Kaimo_File_Server.Core.Services;
using Kaimo_File_Server.Infrastructure.Backup;
using Kaimo_File_Server.Infrastructure.Backup.Restic;
using Kaimo_File_Server.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace Kaimo_File_Server.Tests;

public partial class ResticBackupDatabaseTests
{
    // ══════════════════════════════════════════
    //  Database / Full / pools (phase 4)
    // ══════════════════════════════════════════

    private async Task<(Guid JobId, Guid RunId)> AddRunAsync(MaintenanceFixture fixture, BackupContentLevel level, bool includeVersions,
        string? pool = null, string? shareDir = null)
    {
        await using var db = NewContext();
        var job = await db.BackupJobs.SingleAsync(j => j.Name == "Keep all");
        job.ContentLevel = level;
        job.IncludeVersions = includeVersions;
        if (pool is not null)
            db.BackupJobSources.Add(new BackupJobSource { JobId = job.Id, Kind = BackupSourceKind.Pool, PoolPath = pool });
        if (shareDir is not null)
        {
            var share = new ShareDefinition("docs", shareDir);
            db.ShareDefinitions.Add(share);
            db.BackupJobSources.Add(new BackupJobSource { JobId = job.Id, ShareId = share.Id });
        }
        var run = new BackupRun { RepositoryId = fixture.Repo.Id, JobId = job.Id, Type = BackupRunType.Backup };
        db.BackupRuns.Add(run);
        await db.SaveChangesAsync();
        return (job.Id, run.Id);
    }

    [Fact]
    public async Task Backup_FullLevelWithPool_DumpsTheDatabaseFirst_ThenPoolAndSystemSnapshots()
    {
        var snapshot = 0;
        var runner = new ScriptedRunner(command => command switch
        {
            "backup" => (0, ["{\"message_type\":\"summary\",\"snapshot_id\":\"s" + ++snapshot + "\",\"files_new\":1}"]),
            "snapshots" => (0, ["[]"]),
            _ => (0, []),
        });
        var fixture = await MaintenanceFixtureAsync(runner, appendOnly: false);
        var pool = Path.Combine(fixture.Root, "storage", "pool01");
        Directory.CreateDirectory(Path.Combine(pool, "projects"));
        Directory.CreateDirectory(Path.Combine(pool, ".kaimo-versions"));
        await File.WriteAllTextAsync(Path.Combine(pool, "projects", "a.txt"), "a");
        Directory.CreateDirectory(Path.Combine(fixture.Root, "appdata", ".dp-keys"));
        Directory.CreateDirectory(Path.Combine(fixture.Root, "repo"));
        await using (var db = NewContext())
        {
            db.ShareDefinitions.Add(new ShareDefinition("projects", Path.Combine(pool, "projects")));
            await db.SaveChangesAsync();
        }
        int callsAtDump = -1;
        string? dumpPath = null;
        _database.Setup(d => d.DumpToAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Callback<string, CancellationToken>((path, _) => { callsAtDump = runner.Calls.Count; dumpPath = path; File.WriteAllText(path, "dump"); })
            .Returns(Task.CompletedTask);
        var (jobId, runId) = await AddRunAsync(fixture, BackupContentLevel.Full, includeVersions: true, pool: pool);

        await fixture.Jobs.ExecuteAsync(runId, (_, _) => { }, CancellationToken.None);

        await using var check = NewContext();
        var run = await check.BackupRuns.SingleAsync(r => r.Id == runId);
        Assert.Equal((BackupRunStatus.Succeeded, (string?)null), (run.Status, run.ErrorCode));
        Assert.Equal(1, callsAtDump); // only "unlock" ran before the dump
        var backups = runner.Calls.Where(c => c.Contains("backup")).ToList();
        Assert.Equal(2, backups.Count);

        var poolCall = backups[0];
        Assert.Contains("pool:pool01", poolCall);
        Assert.Contains("level:full", poolCall);
        Assert.Equal("!" + pool.Replace('\\', '/') + "/.kaimo-versions", poolCall[poolCall.LastIndexOf("--exclude") + 1]);
        Assert.Contains(pool, poolCall.Skip(poolCall.IndexOf("--")));

        var systemCall = backups[1];
        Assert.Contains("kind:system", systemCall);
        var paths = systemCall.Skip(systemCall.IndexOf("--") + 1).ToList();
        Assert.Equal([Path.GetDirectoryName(dumpPath)!, Path.Combine(fixture.Root, "appdata")], paths);
        Assert.Contains(Path.Combine(fixture.Root, "appdata").Replace('\\', '/') + "/.kaimo-*", systemCall);
        // Staging (dump with every credential) is gone after the run.
        Assert.False(File.Exists(dumpPath));
        Assert.Contains("\"sources\"", run.DetailsJson);
        Assert.Equal(["s1", "s2"], JsonSerializer.Deserialize<List<string>>(run.SnapshotIdsJson!));
    }

    [Fact]
    public async Task Backup_FailedDatabaseDump_FailsOnlyTheSystemSource()
    {
        var runner = new ScriptedRunner(command => command switch
        {
            "backup" => (0, ["{\"message_type\":\"summary\",\"snapshot_id\":\"s1\"}"]),
            "snapshots" => (0, ["[]"]),
            _ => (0, []),
        });
        var fixture = await MaintenanceFixtureAsync(runner, appendOnly: false);
        var shareDir = Path.Combine(fixture.Root, "share");
        Directory.CreateDirectory(shareDir);
        Directory.CreateDirectory(Path.Combine(fixture.Root, "repo"));
        _database.Setup(d => d.DumpToAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("pg_dump failed with exit code 1: connection refused"));
        var (_, runId) = await AddRunAsync(fixture, BackupContentLevel.FilesAndDatabase, includeVersions: false, shareDir: shareDir);

        await fixture.Jobs.ExecuteAsync(runId, (_, _) => { }, CancellationToken.None);

        await using var check = NewContext();
        var run = await check.BackupRuns.SingleAsync(r => r.Id == runId);
        Assert.Equal((BackupRunStatus.Failed, "database_dump_failed"), (run.Status, run.ErrorCode));
        var backup = Assert.Single(runner.Calls, c => c.Contains("backup"));
        Assert.Contains(shareDir, backup);
        Assert.Contains("connection refused", run.LogExcerpt);
        Assert.Contains(fixture.Published, e => e.Type == "backup.restic.failed");
    }

    [Fact]
    public async Task SaveJob_PoolsAndDatabaseLevels_AreGlobalOnly()
    {
        var (_, _, childShare, _, repo) = await SeedAsync(releaseToParent: true);
        ScopedJobAdmin(childShare.Id);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => Catalog().SaveJobAsync(Actor,
            Draft(repo.Id, childShare.Id) with { ContentLevel = BackupContentLevel.FilesAndDatabase }));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => Catalog().SaveJobAsync(Actor,
            Draft(repo.Id, childShare.Id) with { PoolPaths = ["/data/storage/pool01"] }));
        Assert.Empty(await Catalog().ListSelectablePoolsAsync(Actor));

        // A Global job with the scoped admin's share stays invisible to them.
        await using (var db = NewContext())
        {
            db.BackupJobs.Add(new BackupJob
            {
                Name = "System", RepositoryId = repo.Id, ContentLevel = BackupContentLevel.Full,
                Sources = [new BackupJobSource { ShareId = childShare.Id }],
            });
            await db.SaveChangesAsync();
        }
        Assert.Empty(await Catalog().ListJobsAsync(Actor));
    }

    [Fact]
    public async Task SaveJob_GlobalAdmin_StoresPoolsLevelAndVersions_AndRequiresExistingPools()
    {
        var (_, _, _, _, repo) = await SeedAsync(releaseToParent: false);
        _auth.Setup(a => a.GetAuthorizedShareIdsAsync(It.IsAny<UserContext>(), ManagementPermission.ManageBackupJobs))
            .ReturnsAsync(AuthorizedScopeResult.Unrestricted());
        var storage = Directory.CreateTempSubdirectory("kaimo-pools-").FullName;
        try
        {
            var pool = Path.Combine(storage, "pool01");
            Directory.CreateDirectory(pool);
            var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Storage:RootPath"] = storage }).Build();
            Assert.Equal([pool], await Catalog(config: config).ListSelectablePoolsAsync(Actor));

            var missing = await Assert.ThrowsAsync<ResticException>(() => Catalog(config: config).SaveJobAsync(Actor,
                Draft(repo.Id) with { PoolPaths = [Path.Combine(storage, "elsewhere")] }));
            Assert.Equal("pool_missing", missing.Code);
            var empty = await Assert.ThrowsAsync<ResticException>(() => Catalog(config: config).SaveJobAsync(Actor, Draft(repo.Id)));
            Assert.Equal("sources_required", empty.Code);

            // A pool already contains its shares: shares and pools never mix in one job.
            await using (var db = NewContext())
            {
                db.ShareDefinitions.Add(new ShareDefinition("docs", Path.Combine(pool, "docs")));
                await db.SaveChangesAsync();
            }
            var shareId = (await Catalog(config: config).ListSelectableSharesAsync(Actor)).Single(s => s.Name == "docs").Id;
            var mixed = await Assert.ThrowsAsync<ResticException>(() => Catalog(config: config).SaveJobAsync(Actor,
                Draft(repo.Id, shareId) with { PoolPaths = [pool] }));
            Assert.Equal("sources_mixed", mixed.Code);

            // Database-only job: no file source at all.
            var dbOnly = await Catalog(config: config).SaveJobAsync(Actor, Draft(repo.Id) with { ContentLevel = BackupContentLevel.FilesAndDatabase });
            Assert.Empty(dbOnly.Sources);

            var job = await Catalog(config: config).SaveJobAsync(Actor, Draft(repo.Id) with
            {
                Name = "Pools", ContentLevel = BackupContentLevel.Full, PoolPaths = [pool + Path.DirectorySeparatorChar], IncludeVersions = true,
            });
            await using (var db = NewContext())
            {
                var saved = await db.BackupJobs.Include(j => j.Sources).SingleAsync(j => j.Id == job.Id);
                Assert.Equal((BackupContentLevel.Full, true), (saved.ContentLevel, saved.IncludeVersions));
                var source = Assert.Single(saved.Sources);
                Assert.Equal((BackupSourceKind.Pool, pool), (source.Kind, source.PoolPath));
            }

            // Versions need a database level; a kept pool that is gone meanwhile can still be saved.
            Directory.Delete(pool);
            await Catalog(config: config).SaveJobAsync(Actor, Draft(repo.Id) with
            {
                Id = job.Id, Name = "Pools", PoolPaths = [pool], IncludeVersions = true,
            });
            await using var check = NewContext();
            Assert.False((await check.BackupJobs.SingleAsync(j => j.Id == job.Id)).IncludeVersions);
        }
        finally
        {
            Directory.Delete(storage, recursive: true);
        }
    }

    [Fact]
    public async Task ServerRestore_NeedsRepositoryAdmin_AndOnlyTakesNonSharePoints()
    {
        var (_, _, childShare, _, repo) = await SeedAsync(releaseToParent: true);
        await using (var db = NewContext())
        {
            db.BackupSnapshots.AddRange(
                new BackupSnapshot { RepositoryId = repo.Id, SnapshotId = "sys", Kind = BackupSnapshotKind.System, TimeUtc = DateTime.UtcNow },
                new BackupSnapshot { RepositoryId = repo.Id, SnapshotId = "share", Kind = BackupSnapshotKind.Share, ShareId = childShare.Id, TimeUtc = DateTime.UtcNow });
            await db.SaveChangesAsync();
        }
        ScopedJobAdmin(childShare.Id);
        Assert.Empty(await Catalog().ListServerBackupPointsAsync(Actor));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => Catalog().EnqueueServerRestoreAsync(Actor, repo.Id, "sys"));

        _auth.Setup(a => a.HasGlobalPermissionAsync(It.IsAny<UserContext>(), ManagementPermission.ManageBackupRepositories)).ReturnsAsync(true);
        var point = Assert.Single(await Catalog().ListServerBackupPointsAsync(Actor));
        Assert.Equal(("sys", BackupSnapshotKind.System), (point.SnapshotId, point.Kind));
        var ex = await Assert.ThrowsAsync<ResticException>(() => Catalog().EnqueueServerRestoreAsync(Actor, repo.Id, "share"));
        Assert.Equal("request_invalid", ex.Code);
        await Assert.ThrowsAsync<ReadOnlyDemoException>(() => Catalog(demo: true).EnqueueServerRestoreAsync(Actor, repo.Id, "sys"));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Maintenance_TestRestore_ReadsBackTheNewestDatabaseDump(bool readable)
    {
        const string system = "/data/kaimo-backups/restic-meta/system";
        var runner = new ScriptedRunner(command => command switch
        {
            "forget" => (0, ["[]"]),
            "snapshots" => (0, ["[{\"id\":\"sys1\",\"time\":\"2026-10-11T20:00:00Z\",\"paths\":[\"" + system
                                + "\"],\"tags\":[\"kaimo\",\"level:db\",\"kind:system\"],\"summary\":{\"total_files_processed\":2,\"total_bytes_processed\":10}}]"]),
            _ => (0, []),
        });
        var fixture = await MaintenanceFixtureAsync(runner, appendOnly: false);
        await using (var db = NewContext())
        {
            (await db.BackupRepositories.SingleAsync(r => r.Id == fixture.Repo.Id)).TestRestoreSampleSize = 5;
            await db.SaveChangesAsync();
        }
        string? checkedFile = null;
        _database.Setup(d => d.VerifyDumpAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Callback<string, CancellationToken>((path, _) => checkedFile = path)
            .ReturnsAsync(readable);

        await fixture.Executor.ExecuteAsync(fixture.Repo.Id, BackupRunTrigger.Manual, null, (_, _) => { }, CancellationToken.None);

        var dump = Assert.Single(runner.Calls, c => c.Contains("dump"));
        Assert.Equal(["sys1:" + system, "/kaimo-database.dump"], dump.Skip(dump.IndexOf("--") + 1));
        Assert.EndsWith("kaimo-database.dump", checkedFile);
        Assert.False(File.Exists(checkedFile)); // staging holds every credential: removed afterwards

        await using var check = NewContext();
        var test = await check.BackupRuns.SingleAsync(r => r.Type == BackupRunType.TestRestore);
        Assert.Equal(readable, BackupMaintenanceDetails.TryParse(test.DetailsJson)!.DatabaseDumpOk);
        Assert.Equal(readable ? BackupRunStatus.Succeeded : BackupRunStatus.Failed, test.Status);
        Assert.Equal(!readable, fixture.Published.Any(e => e.Type == Core.Services.Notifications.NotificationEventCatalog.FileBackupTestRestoreMismatch));
    }

    [Fact]
    public async Task Backup_StoresTheSystemSourceAsKey_AndRetentionAddsTheSystemPass()
    {
        var jobTag = "";
        var runner = new ScriptedRunner(command => command switch
        {
            "backup" => (0, ["{\"message_type\":\"summary\",\"snapshot_id\":\"s1\"}"]),
            "snapshots" => (0, ["[{\"id\":\"s1\",\"time\":\"2026-10-11T20:00:00Z\",\"paths\":[\"/x/system\"],\"tags\":[\"kaimo\",\"job:" + jobTag + "\",\"kind:system\"]}]"]),
            "forget" => (0, ["[]"]),
            _ => (0, []),
        });
        var fixture = await MaintenanceFixtureAsync(runner, appendOnly: false);
        Directory.CreateDirectory(Path.Combine(fixture.Root, "repo"));
        _database.Setup(d => d.DumpToAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Callback<string, CancellationToken>((path, _) => File.WriteAllText(path, "dump"))
            .Returns(Task.CompletedTask);
        Guid runId, jobId;
        await using (var db = NewContext())
        {
            var job = await db.BackupJobs.SingleAsync(j => j.Name == "Nightly");
            job.ContentLevel = BackupContentLevel.FilesAndDatabase;
            var run = new BackupRun { RepositoryId = fixture.Repo.Id, JobId = job.Id, Type = BackupRunType.Backup };
            db.BackupRuns.Add(run);
            await db.SaveChangesAsync();
            (runId, jobId) = (run.Id, job.Id);
        }
        // The scripted snapshot list must carry the real job tag to land in the cache as this job's point.
        jobTag = jobId.ToString("N");

        await fixture.Jobs.ExecuteAsync(runId, (_, _) => { }, CancellationToken.None);

        await using var check = NewContext();
        var backup = await check.BackupRuns.SingleAsync(r => r.Id == runId);
        Assert.Contains("\"" + BackupJobExecutor.SystemSourceKey + "\"", backup.DetailsJson);
        var forgets = runner.Calls.Where(c => c.Contains("forget")).ToList();
        Assert.Equal(2, forgets.Count);
        Assert.Contains("job:" + jobId.ToString("N") + ",kind:system", forgets[1]);
    }

    [Fact]
    public async Task Rclone_TokenRefreshedDuringARun_IsStoredBack_ButNeverFromProbedSecrets()
    {
        var vault = new Web.Services.DataProtectionCredentialVault(
            Microsoft.AspNetCore.DataProtection.DataProtectionProvider.Create("KaimoRcloneTests"));
        var tempRoot = Path.Combine(Path.GetTempPath(), "kaimo-rclone-" + Guid.NewGuid().ToString("N"));
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Backup:Restic:TempDirectory"] = tempRoot }).Build();
        var resolver = new ResticTargetResolver(DbFactory, vault, config);
        var repo = new BackupRepository
        {
            Name = "OneDrive",
            Backend = BackupBackend.Rclone,
            State = BackupRepositoryState.Active,
            SettingsJson = new ResticRepositorySettings { Path = "kaimo" }.Serialize(),
        };
        repo.EncryptedPassword = resolver.ProtectPassword(repo.Id, "pw");
        repo.EncryptedSecrets = resolver.ProtectSecrets(repo.Id, new ResticBackendSecrets { RcloneConfig = "[od]\ntype = onedrive\ntoken = OLD" });
        await using (var db = NewContext())
        {
            db.BackupRepositories.Add(repo);
            await db.SaveChangesAsync();
        }

        // rclone rewrites its config file while restic runs (simulated).
        void RcloneRefreshes(ResticTarget target, string token)
            => File.WriteAllText(target.Environment["RCLONE_CONFIG"], $"[kaimo]\ntype = onedrive\ntoken = {token}\n\n");
        async Task<string?> StoredConfigAsync()
        {
            await using var db = NewContext();
            return resolver.ReadSecrets(await db.BackupRepositories.SingleAsync(r => r.Id == repo.Id)).RcloneConfig;
        }

        using (var probe = await resolver.ResolveAsync(repo, new ResticBackendSecrets { RcloneConfig = "type = onedrive\ntoken = TYPED" }, "pw", default))
            RcloneRefreshes(probe, "PROBED");
        Assert.Contains("token = OLD", await StoredConfigAsync());

        using (var run = await resolver.ResolveAsync(repo, default))
            RcloneRefreshes(run, "NEW");
        Assert.Equal("[kaimo]\ntype = onedrive\ntoken = NEW\n", await StoredConfigAsync());

        // A rewritten config that would no longer pass validation is ignored.
        using (var run = await resolver.ResolveAsync(repo, default))
            File.WriteAllText(run.Environment["RCLONE_CONFIG"], "[kaimo]\ntype = local\n");
        Assert.Contains("token = NEW", await StoredConfigAsync());
        Assert.False(Directory.EnumerateFileSystemEntries(tempRoot).Any());
    }
}

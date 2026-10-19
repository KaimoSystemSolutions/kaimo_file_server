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
    // ─────────────── integrity (phase 3) ───────────────

    [Fact]
    public void ForgetArgs_GroupByHostAndPaths_PerJobTag_AndOnlyActiveRules()
    {
        var job = Guid.NewGuid();
        var args = ResticClient.BuildForgetArgs(new BackupRetention { KeepDaily = 7, KeepMonthly = 12 }, job);

        Assert.Equal("forget", args[0]);
        Assert.Equal("job:" + job.ToString("N"), args[args.IndexOf("--tag") + 1]);
        // Never by tags: the unique run tag would make every snapshot its own group.
        Assert.Equal("host,paths", args[args.IndexOf("--group-by") + 1]);
        Assert.Equal("7", args[args.IndexOf("--keep-daily") + 1]);
        Assert.Equal("12", args[args.IndexOf("--keep-monthly") + 1]);
        Assert.DoesNotContain("--keep-last", args);
        Assert.DoesNotContain("--prune", args);
    }

    [Fact]
    public void ForgetArgs_SystemPass_GroupsAllSystemPointsOfTheJobTogether()
    {
        var job = Guid.NewGuid();
        var args = ResticClient.BuildForgetArgs(new BackupRetention { KeepLast = 3 }, job, systemPointsOnly: true);

        // One tag list = both tags required; no paths grouping, so a changed content level
        // (other system paths) does not leave an old group behind.
        Assert.Equal("job:" + job.ToString("N") + ",kind:system", args[args.IndexOf("--tag") + 1]);
        Assert.Equal("host", args[args.IndexOf("--group-by") + 1]);
        Assert.Equal("3", args[args.IndexOf("--keep-last") + 1]);
    }

    [Fact]
    public void GuardSource_UnmountedPoolWithRecreatedFolders_CountsAsEmpty()
    {
        var pool = Directory.CreateTempSubdirectory("kaimo-pool-guard-").FullName;
        try
        {
            // What Kaimo recreates on an unmounted pool: share folders, their internal folders,
            // home folders, plus the pool's own version store.
            Directory.CreateDirectory(Path.Combine(pool, "projects", ".kaimo-close-captures"));
            var home = Directory.CreateDirectory(Path.Combine(pool, "users", Guid.NewGuid().ToString())).FullName;
            Directory.CreateDirectory(Path.Combine(pool, ".kaimo-versions", "ab"));
            File.WriteAllText(Path.Combine(pool, ".kaimo-versions", "ab", "blob"), "x");
            File.WriteAllText(Path.Combine(pool, "projects", ".kaimo-close-captures", "c"), "x");

            Assert.Equal("source_empty", Assert.Throws<ResticException>(() =>
                BackupJobExecutor.GuardSource(pool, hadContentBefore: true)).Code);
            BackupJobExecutor.GuardSource(pool, hadContentBefore: false); // a new, empty pool is fine

            File.WriteAllText(Path.Combine(home, "notes.txt"), "x");
            BackupJobExecutor.GuardSource(pool, hadContentBefore: true);
        }
        finally
        {
            Directory.Delete(pool, recursive: true);
        }
    }

    [Fact]
    public void ForgetArgs_WithoutAnyRule_AreRefused()
    {
        var ex = Assert.Throws<ResticException>(() => ResticClient.BuildForgetArgs(new BackupRetention(), Guid.NewGuid()));
        Assert.Equal("retention_empty", ex.Code);
    }

    [Fact]
    public async Task Forget_CountsRemovedSnapshotsOfAllGroups()
    {
        var runner = new FakeRunner(0,
        [
            """[{"tags":["job:x"],"host":"kaimo","paths":["/a"],"keep":[{"id":"1"}],"remove":[{"id":"2"},{"id":"3"}]},{"host":"kaimo","paths":["/b"],"keep":[{"id":"4"}],"remove":null}]""",
        ]);
        var removed = await Client(runner).ForgetAsync(Target(), Guid.NewGuid(), new BackupRetention { KeepLast = 1 }, CancellationToken.None);

        Assert.Equal(2, removed);
        Assert.Equal("30m", runner.Arguments[runner.Arguments.IndexOf("--retry-lock") + 1]);
    }

    [Fact]
    public async Task Check_Success_AndReadDataSubset()
    {
        var runner = new FakeRunner(0, ["no errors were found"]);
        var result = await Client(runner).CheckAsync(Target(), "3/12", CancellationToken.None);

        Assert.True(result.Ok);
        Assert.Equal("3/12", runner.Arguments[runner.Arguments.IndexOf("--read-data-subset") + 1]);
    }

    [Fact]
    public async Task Check_FoundDamage_IsAResult_NotAnException()
    {
        // Shape of restic 0.19.1 with a deleted pack: summary on stdout, findings on stderr. The
        // "permission denied" inside a finding must not turn the damage into an access error.
        var runner = new FakeRunner(1,
            ["""{"message_type":"summary","num_errors":2,"broken_packs":null,"suggest_repair_index":false,"suggest_prune":false}"""],
            """
            {"message_type":"error","message":"pack f6c4e2d3: does not exist\n"}
            {"message_type":"error","message":"Load(<data/f6c4e2d3>, 0, 0) failed: open /r/data/f6/f6c4e2d3: permission denied"}
            {"message_type":"exit_error","code":1,"message":"Fatal: repository contains errors"}
            """);
        var result = await Client(runner).CheckAsync(Target(), null, CancellationToken.None);

        Assert.False(result.Ok);
        Assert.Equal(2, result.Errors.Count);
        Assert.Equal("pack f6c4e2d3: does not exist", result.Errors[0]);
        Assert.Contains("does not exist", result.OutputTail);
        Assert.DoesNotContain("--read-data-subset", runner.Arguments);
    }

    [Fact]
    public async Task Check_UnreachableRepository_StillThrows()
    {
        var runner = new FakeRunner(1, [], "Fatal: unable to open repository: dial tcp: lookup nas: no such host");
        var ex = await Assert.ThrowsAsync<ResticException>(() => Client(runner).CheckAsync(Target(), null, CancellationToken.None));
        Assert.Equal("repo_unreachable", ex.Code);
    }

    [Fact]
    public async Task Check_AbortWithoutReportedErrors_IsNoDamageVerdict()
    {
        // E.g. a network drop mid-check: exit 1, no summary — must not read as "repository damaged".
        var runner = new FakeRunner(1, [], """{"message_type":"exit_error","code":1,"message":"Fatal: load index: read tcp: connection reset by peer"}""");
        var ex = await Assert.ThrowsAsync<ResticException>(() => Client(runner).CheckAsync(Target(), null, CancellationToken.None));
        Assert.Equal("restic_failed", ex.Code);
    }

    private static ResticTarget Target(string url, Dictionary<string, string> env, params string[] args)
        => new(url, new Dictionary<string, string>(env) { ["RESTIC_REPOSITORY"] = url }, args, null);

    [Fact]
    public void CopyTarget_PutsTheSourceIntoFromVariables_AndMergesBackendOptions()
    {
        var mirror = Target("s3:https://s3.example.com/mirror",
            new() { ["RESTIC_PASSWORD"] = "mirror-pw", ["AWS_ACCESS_KEY_ID"] = "k", ["AWS_SECRET_ACCESS_KEY"] = "s" },
            "-o", "s3.bucket-lookup=path", "--cacert", "/tmp/a/ca.pem");
        var source = Target("sftp:u@nas:/backup",
            new() { ["RESTIC_PASSWORD"] = "source-pw" },
            "-o", "sftp.command=ssh -i /tmp/b/id_key u@nas -s sftp", "--cacert", "/tmp/b/ca.pem");

        var merged = ResticClient.BuildCopyTarget(mirror, source);

        Assert.Equal("s3:https://s3.example.com/mirror", merged.Environment["RESTIC_REPOSITORY"]);
        Assert.Equal("mirror-pw", merged.Environment["RESTIC_PASSWORD"]);
        Assert.Equal("sftp:u@nas:/backup", merged.Environment["RESTIC_FROM_REPOSITORY"]);
        Assert.Equal("source-pw", merged.Environment["RESTIC_FROM_PASSWORD"]);
        Assert.Equal("k", merged.Environment["AWS_ACCESS_KEY_ID"]);
        Assert.Contains("sftp.command=ssh -i /tmp/b/id_key u@nas -s sftp", merged.GlobalArguments);
        Assert.Contains("s3.bucket-lookup=path", merged.GlobalArguments);
        Assert.Contains("/tmp/a/ca.pem", merged.GlobalArguments);
        Assert.Contains("/tmp/b/ca.pem", merged.GlobalArguments);
    }

    [Fact]
    public void CopyTarget_RejectsDifferentCredentialsUnderTheSameName()
    {
        var a = Target("s3:https://a/x", new() { ["RESTIC_PASSWORD"] = "1", ["AWS_ACCESS_KEY_ID"] = "k1" });
        var b = Target("s3:https://b/y", new() { ["RESTIC_PASSWORD"] = "2", ["AWS_ACCESS_KEY_ID"] = "k2" });
        Assert.Equal("mirror_conflict", Assert.Throws<ResticException>(() => ResticClient.BuildCopyTarget(a, b)).Code);
    }

    [Fact]
    public void CopyTarget_SameSshConnection_UsesTheDestinationCommand()
    {
        // Same connection, resolved twice: only the temporary key path differs.
        var a = Target("sftp:u@nas:/m", new() { ["RESTIC_PASSWORD"] = "1" }, "-o", "sftp.command=ssh -i /tmp/a/id_key u@nas -s sftp");
        var b = Target("sftp:u@nas:/s", new() { ["RESTIC_PASSWORD"] = "2" }, "-o", "sftp.command=ssh -i /tmp/b/id_key u@nas -s sftp");

        var merged = ResticClient.BuildCopyTarget(a, b);

        Assert.Equal(["-o", "sftp.command=ssh -i /tmp/a/id_key u@nas -s sftp"], merged.GlobalArguments);
    }

    private static BackupRepository Repo(BackupBackend backend, ResticRepositorySettings settings)
        => new() { Name = "r", Backend = backend, SettingsJson = settings.Serialize() };

    [Fact]
    public void ValidateMirror_SameBackendNeedsSameCredentials_DifferentBackendsAlwaysWork()
    {
        var s3 = new ResticRepositorySettings { Endpoint = "https://s3", Bucket = "b1" };
        var keysA = new ResticBackendSecrets { S3AccessKeyId = "a", S3SecretAccessKey = "x" };
        var keysB = new ResticBackendSecrets { S3AccessKeyId = "b", S3SecretAccessKey = "y" };
        var local = Repo(BackupBackend.Local, new ResticRepositorySettings { Path = "/data/kaimo-backups/restic/a" });

        ResticBackend.ValidateMirror(Repo(BackupBackend.S3, s3), keysA, Repo(BackupBackend.S3, s3), keysA);
        ResticBackend.ValidateMirror(Repo(BackupBackend.S3, s3), keysA, local, new ResticBackendSecrets());
        Assert.Equal("mirror_conflict", Assert.Throws<ResticException>(() =>
            ResticBackend.ValidateMirror(Repo(BackupBackend.S3, s3), keysA, Repo(BackupBackend.S3, s3), keysB)).Code);

        var connection = Guid.NewGuid();
        var sftp = Repo(BackupBackend.Sftp, new ResticRepositorySettings { StorageConnectionId = connection, Path = "a" });
        ResticBackend.ValidateMirror(sftp, new(), Repo(BackupBackend.Sftp, new ResticRepositorySettings { StorageConnectionId = connection, Path = "b" }), new());
        Assert.Throws<ResticException>(() =>
            ResticBackend.ValidateMirror(sftp, new(), Repo(BackupBackend.Sftp, new ResticRepositorySettings { StorageConnectionId = Guid.NewGuid(), Path = "b" }), new()));

        Assert.Equal("mirror_invalid", Assert.Throws<ResticException>(() => ResticBackend.ValidateMirror(local, new(), local, new())).Code);
    }

    [Fact]
    public void RestorePathsArgs_IncludeEscapedAbsolutePaths_SnapshotAfterSeparator()
    {
        var args = ResticClient.BuildRestorePathsArgs("abc", ["/data/p/report [v2].pdf", "/data/p/a.txt"], "/stage");

        Assert.Equal("/stage", args[args.IndexOf("--target") + 1]);
        Assert.Contains("--verify", args);
        Assert.Contains(@"/data/p/report \[v2].pdf", args);
        Assert.Equal(["--", "abc"], args.TakeLast(2));
        Assert.Throws<ResticException>(() => ResticClient.BuildRestorePathsArgs("abc", ["relative/a.txt"], "/stage"));
        Assert.Throws<ResticException>(() => ResticClient.BuildRestorePathsArgs("abc", ["/data/../etc/passwd"], "/stage"));
    }

    private static ResticNode FileNode(string path, long size = 10) => new(Path.GetFileName(path), path, false, size, null);
    private static ResticNode DirNode(string path) => new(Path.GetFileName(path), path, true, 0, null);

    [Fact]
    public async Task SampleFiles_DescendsRandomly_ListsEachFolderOnce_AndSkipsLargeFiles()
    {
        var tree = new Dictionary<string, IReadOnlyList<ResticNode>>
        {
            ["/s"] = [DirNode("/s/a"), DirNode("/s/empty"), FileNode("/s/top.txt"), FileNode("/s/huge.iso", BackupMaintenanceExecutor.MaximumSampleFileBytes + 1)],
            ["/s/a"] = [FileNode("/s/a/1.txt"), FileNode("/s/a/2.txt"), DirNode("/s/a/b")],
            ["/s/a/b"] = [FileNode("/s/a/b/3.txt")],
            ["/s/empty"] = [],
        };
        var listed = new List<string>();
        Task<IReadOnlyList<ResticNode>> List(string dir)
        {
            listed.Add(dir);
            return Task.FromResult(tree[dir]);
        }

        var files = await BackupMaintenanceExecutor.SampleFilesAsync(List, "/s", 3, new Random(7));

        Assert.Equal(3, files.Count);
        Assert.Equal(3, files.Select(f => f.Path).Distinct().Count());
        Assert.DoesNotContain(files, f => f.Path == "/s/huge.iso");
        Assert.Equal(listed.Count, listed.Distinct().Count());

        // More wanted than exist: every file once, and the attempts stay bounded.
        var all = await BackupMaintenanceExecutor.SampleFilesAsync(List, "/s", 50, new Random(7));
        Assert.Equal(["/s/a/1.txt", "/s/a/2.txt", "/s/a/b/3.txt", "/s/top.txt"], all.Select(f => f.Path).Order());
        Assert.Empty(await BackupMaintenanceExecutor.SampleFilesAsync(List, "/s/empty", 5, new Random(7)));
    }

    [Fact]
    public void Stale_OnlyScheduledEnabledJobsPastTheirThreshold()
    {
        var now = Utc(2026, 10, 11, 12);
        BackupJob Job(DateTime? lastSuccess, int alertDays = 3, bool scheduled = true, bool enabled = true, DateTime? changed = null) => new()
        {
            Enabled = enabled,
            AlertAfterDays = alertDays,
            Schedule = scheduled ? Daily((2, 0)) : new BackupSchedule(),
            CreatedAtUtc = now.AddDays(-30),
            UpdatedAtUtc = changed ?? now.AddDays(-30),
            LastSuccessAtUtc = lastSuccess,
        };

        Assert.True(BackupSchedulerService.IsStale(Job(now.AddDays(-4)), now));
        Assert.True(BackupSchedulerService.IsStale(Job(null), now));
        Assert.False(BackupSchedulerService.IsStale(Job(now.AddDays(-2)), now));
        Assert.False(BackupSchedulerService.IsStale(Job(now.AddDays(-4), alertDays: 0), now));
        Assert.False(BackupSchedulerService.IsStale(Job(now.AddDays(-4), scheduled: false), now));
        Assert.False(BackupSchedulerService.IsStale(Job(now.AddDays(-4), enabled: false), now));
        // Re-enabled (or rescheduled) yesterday after weeks without success: a fresh grace period.
        Assert.False(BackupSchedulerService.IsStale(Job(now.AddDays(-20), changed: now.AddDays(-1)), now));
    }

    [Fact]
    public void Stale_WeeklyJob_IsNotOverdueBeforeItMissedItsWeeklyRun()
    {
        var now = Utc(2026, 10, 11, 12);
        var weekly = new BackupJob
        {
            AlertAfterDays = 3,
            Schedule = new BackupSchedule { Times = [new TimeOnly(2, 0)], Days = [DayOfWeek.Sunday] },
            CreatedAtUtc = now.AddDays(-60),
            UpdatedAtUtc = now.AddDays(-60),
        };

        Assert.Equal(8, BackupSchedulerService.EffectiveAlertDays(weekly));
        weekly.LastSuccessAtUtc = now.AddDays(-6);
        Assert.False(BackupSchedulerService.IsStale(weekly, now));
        weekly.LastSuccessAtUtc = now.AddDays(-9);
        Assert.True(BackupSchedulerService.IsStale(weekly, now));
        // Mon + Thu: longest gap Thu → Mon = 4 days, so 5 days beat the configured 3.
        Assert.Equal(5, BackupScheduleCalculator.LongestGapDays(new BackupSchedule
        {
            Times = [new TimeOnly(2, 0)], Days = [DayOfWeek.Monday, DayOfWeek.Thursday],
        }) + 1);
        Assert.Equal(1, BackupScheduleCalculator.LongestGapDays(Daily((2, 0))));
    }

    [Fact]
    public void Upstream_FollowsMirrorChains()
    {
        Guid a = Guid.NewGuid(), b = Guid.NewGuid(), c = Guid.NewGuid(), other = Guid.NewGuid();
        var targets = new Dictionary<Guid, Guid?> { [a] = b, [b] = c, [c] = null, [other] = null };

        Assert.Equal(new HashSet<Guid> { a, b, c }, BackupMaintenanceExecutor.UpstreamOf(c, targets));
        Assert.Equal(new HashSet<Guid> { a }, BackupMaintenanceExecutor.UpstreamOf(a, targets));
    }

    [Fact]
    public void ParseNode_ReadsNanosecondMtimes()
    {
        // restic 0.19.1 prints nine fractional digits; the test restore compares this mtime with the live file.
        var node = ResticClient.ParseNode("""{"name":"a.txt","type":"file","path":"/tmp/data/a.txt","size":33,"mtime":"2026-10-11T15:48:48.088384705Z","message_type":"node"}""");

        Assert.NotNull(node?.ModifiedUtc);
        Assert.Equal(new DateTime(2026, 10, 11, 15, 48, 48, DateTimeKind.Utc).AddTicks(883847), node!.ModifiedUtc!.Value);
    }

    [Fact]
    public void DefaultMaintenance_MatchesTheMigrationDefault()
    {
        // AddBackupIntegrity writes exactly this literal into existing repositories.
        Assert.Equal("{\"Times\":[\"03:00:00\"],\"Days\":[0]}", BackupSchedule.Serialize(BackupSchedule.DefaultMaintenance()));
        Assert.True(BackupRetention.Deserialize("{}").IsEmpty);
    }
}

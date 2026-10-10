using Xunit;
using Kaimo_File_Server.Core.Domain;
using Kaimo_File_Server.Core.Domain.Backup;
using Kaimo_File_Server.Core.Helpers;
using Kaimo_File_Server.Infrastructure.Backup;
using Kaimo_File_Server.Infrastructure.Backup.Restic;
using Kaimo_File_Server.Infrastructure.ExternalStorage;
using Microsoft.Extensions.Configuration;

namespace Kaimo_File_Server.Tests;

/// <summary>
/// Pure logic of the restic file backup: schedule math, backend mapping, CLI arguments,
/// exit-code mapping, output parsing, source guards and release/visibility rules.
/// </summary>
public class ResticBackupTests
{
    // ─────────────── Schedule ───────────────

    private static readonly TimeZoneInfo Berlin = TimeZoneInfo.FindSystemTimeZoneById("Europe/Berlin");
    private static DateTime Utc(int y, int mo, int d, int h, int mi = 0) => new(y, mo, d, h, mi, 0, DateTimeKind.Utc);
    private static BackupSchedule Daily(params (int H, int M)[] times) => new()
    {
        Times = times.Select(t => new TimeOnly(t.H, t.M)).ToList(),
        Days = Enum.GetValues<DayOfWeek>().ToList(),
    };

    [Fact]
    public void FindDueSlot_ReturnsSlotThatJustPassed()
    {
        // 02:00 Berlin (CEST, UTC+2) on 2026-10-05 = 00:00 UTC.
        var slot = BackupScheduleCalculator.FindDueSlot(Daily((2, 0)), Utc(2026, 10, 5, 1, 30), Berlin,
            Utc(2026, 10, 4, 12), TimeSpan.FromDays(7));
        Assert.Equal(Utc(2026, 10, 5, 0), slot);
    }

    [Fact]
    public void FindDueSlot_NothingDueBeforeTheNextSlot()
    {
        var slot = BackupScheduleCalculator.FindDueSlot(Daily((2, 0)), Utc(2026, 10, 4, 23, 30), Berlin,
            Utc(2026, 10, 4, 12), TimeSpan.FromDays(7));
        Assert.Null(slot);
    }

    [Fact]
    public void FindDueSlot_SeveralMissedSlots_CollapseIntoTheNewestOne()
    {
        var slot = BackupScheduleCalculator.FindDueSlot(Daily((2, 0), (14, 0)), Utc(2026, 10, 5, 1), Berlin,
            Utc(2026, 10, 1, 0, 30), TimeSpan.FromDays(7));
        Assert.Equal(Utc(2026, 10, 5, 0), slot);
    }

    [Fact]
    public void FindDueSlot_IgnoresSlotsOlderThanTheCatchUpWindow()
    {
        // Mondays only; on Sunday 2026-10-04 the last Monday (09-28) is 6.5 days ago.
        var schedule = new BackupSchedule { Times = [new TimeOnly(2, 0)], Days = [DayOfWeek.Monday] };
        Assert.Null(BackupScheduleCalculator.FindDueSlot(schedule, Utc(2026, 10, 4, 12), Berlin,
            Utc(2026, 9, 1, 0), TimeSpan.FromDays(3)));
        Assert.Equal(Utc(2026, 9, 28, 0), BackupScheduleCalculator.FindDueSlot(schedule, Utc(2026, 10, 4, 12), Berlin,
            Utc(2026, 9, 1, 0), TimeSpan.FromDays(7)));
    }

    [Fact]
    public void FindDueSlot_HonorsWeekdayFilter()
    {
        var schedule = new BackupSchedule { Times = [new TimeOnly(2, 0)], Days = [DayOfWeek.Tuesday] };
        // Monday 2026-10-05 03:00 local: no Tuesday slot since the last handled one.
        Assert.Null(BackupScheduleCalculator.FindDueSlot(schedule, Utc(2026, 10, 5, 1), Berlin,
            Utc(2026, 9, 30, 0), TimeSpan.FromDays(7)));
    }

    [Fact]
    public void FindDueSlot_SkipsStartTimeInsideTheDstGap()
    {
        // 2026-03-29 02:30 does not exist in Berlin (02:00 → 03:00).
        Assert.Null(BackupScheduleCalculator.FindDueSlot(Daily((2, 30)), Utc(2026, 3, 29, 10), Berlin,
            Utc(2026, 3, 28, 12), TimeSpan.FromDays(7)));
    }

    [Fact]
    public void FindDueSlot_RunsAmbiguousStartTimeOnceAtItsFirstOccurrence()
    {
        // 2026-10-25 02:30 exists twice (CEST 00:30 UTC, CET 01:30 UTC).
        var first = BackupScheduleCalculator.FindDueSlot(Daily((2, 30)), Utc(2026, 10, 25, 3), Berlin,
            Utc(2026, 10, 24, 12), TimeSpan.FromDays(7));
        Assert.Equal(Utc(2026, 10, 25, 0, 30), first);
        Assert.Null(BackupScheduleCalculator.FindDueSlot(Daily((2, 30)), Utc(2026, 10, 25, 3), Berlin,
            first!.Value, TimeSpan.FromDays(7)));
    }

    [Fact]
    public void FindDueSlot_EmptySchedule_NeverRuns()
        => Assert.Null(BackupScheduleCalculator.FindDueSlot(new BackupSchedule(), Utc(2026, 10, 5, 1), Berlin,
            Utc(2026, 1, 1, 0), TimeSpan.FromDays(7)));

    [Fact]
    public void NextSlot_ReturnsTheNextStart()
        => Assert.Equal(Utc(2026, 10, 5, 12), BackupScheduleCalculator.NextSlot(Daily((2, 0), (14, 0)), Utc(2026, 10, 5, 1), Berlin));

    // ─────────────── Backend mapping ───────────────

    private static readonly string LocalRoot = Path.Combine(Path.GetTempPath(), "kaimo-restic-roots");

    [Fact]
    public void Local_BuildsPathUrl_AndPasswordOnlyInEnvironment()
    {
        var settings = new ResticRepositorySettings { Path = Path.Combine(LocalRoot, "repo1") };
        ResticBackend.Validate(BackupBackend.Local, settings, new ResticBackendSecrets(), [LocalRoot]);
        var url = ResticBackend.BuildUrl(BackupBackend.Local, settings);
        var env = ResticBackend.BuildEnvironment(BackupBackend.Local, settings, new ResticBackendSecrets(), "pw-secret", url);

        Assert.Equal(Path.Combine(LocalRoot, "repo1"), url);
        Assert.Equal("pw-secret", env["RESTIC_PASSWORD"]);
        Assert.Equal(url, env["RESTIC_REPOSITORY"]);
    }

    [Fact]
    public void Local_RejectsPathsOutsideTheRootsOrWithTraversal()
    {
        var outside = new ResticRepositorySettings { Path = Path.Combine(Path.GetTempPath(), "elsewhere", "repo") };
        Assert.Equal("path_outside_roots", Assert.Throws<ResticException>(() =>
            ResticBackend.Validate(BackupBackend.Local, outside, new(), [LocalRoot])).Code);

        var traversal = new ResticRepositorySettings { Path = LocalRoot + Path.DirectorySeparatorChar + ".." + Path.DirectorySeparatorChar + "x" };
        Assert.Equal("path_invalid", Assert.Throws<ResticException>(() =>
            ResticBackend.Validate(BackupBackend.Local, traversal, new(), [LocalRoot])).Code);

        // The root itself is not a repository location.
        Assert.Throws<ResticException>(() =>
            ResticBackend.Validate(BackupBackend.Local, new ResticRepositorySettings { Path = LocalRoot }, new(), [LocalRoot]));
    }

    [Fact]
    public void Rest_KeepsCredentialsOutOfTheUrl()
    {
        var settings = new ResticRepositorySettings { Endpoint = "https://backup.example.com:8000/", Path = "kaimo" };
        var secrets = new ResticBackendSecrets { RestUsername = "kaimo", RestPassword = "rest-secret" };
        ResticBackend.Validate(BackupBackend.Rest, settings, secrets, []);
        var url = ResticBackend.BuildUrl(BackupBackend.Rest, settings);
        var env = ResticBackend.BuildEnvironment(BackupBackend.Rest, settings, secrets, "pw", url);

        Assert.Equal("rest:https://backup.example.com:8000/kaimo", url);
        Assert.DoesNotContain("rest-secret", url);
        Assert.Equal("rest-secret", env["RESTIC_REST_PASSWORD"]);
    }

    [Theory]
    [InlineData("https://user:pw@backup.example.com")]
    [InlineData("ftp://backup.example.com")]
    [InlineData("https://backup.example.com/?x=1")]
    [InlineData("not a url")]
    public void Rest_RejectsUnsafeEndpoints(string endpoint)
        => Assert.Equal("endpoint_invalid", Assert.Throws<ResticException>(() =>
            ResticBackend.Validate(BackupBackend.Rest, new ResticRepositorySettings { Endpoint = endpoint }, new(), [])).Code);

    [Fact]
    public void S3_BuildsBucketUrl_AndRequiresKeys()
    {
        var settings = new ResticRepositorySettings { Endpoint = "https://s3.example.com", Bucket = "kaimo-backup", Path = "site-a", Region = "eu-central-1" };
        var secrets = new ResticBackendSecrets { S3AccessKeyId = "AK", S3SecretAccessKey = "SK" };
        ResticBackend.Validate(BackupBackend.S3, settings, secrets, []);
        var url = ResticBackend.BuildUrl(BackupBackend.S3, settings);
        var env = ResticBackend.BuildEnvironment(BackupBackend.S3, settings, secrets, "pw", url);

        Assert.Equal("s3:https://s3.example.com/kaimo-backup/site-a", url);
        Assert.Equal("AK", env["AWS_ACCESS_KEY_ID"]);
        Assert.Equal("SK", env["AWS_SECRET_ACCESS_KEY"]);
        Assert.Equal("eu-central-1", env["AWS_DEFAULT_REGION"]);
        Assert.Equal("credentials_required", Assert.Throws<ResticException>(() =>
            ResticBackend.Validate(BackupBackend.S3, settings, new ResticBackendSecrets(), [])).Code);
    }

    [Fact]
    public void Sftp_UsesThePinnedSshCommand()
    {
        var ssh = new RsyncSshConnectionSettings("nas.example.com", 2222, "backup", "/volume1", "SHA256:abcdefghijklmnopqrstuvwxyz",
            "/data/kaimo-system/.kaimo-restic-tmp/x/id_key", "/data/kaimo-system/known_hosts");
        var command = ResticBackend.BuildSftpCommand(ssh);
        var url = ResticBackend.BuildUrl(BackupBackend.Sftp, new ResticRepositorySettings { Path = "kaimo" }, ssh);

        Assert.Contains("StrictHostKeyChecking=yes", command);
        Assert.Contains("UserKnownHostsFile=/data/kaimo-system/known_hosts", command);
        Assert.EndsWith("backup@nas.example.com -s sftp", command);
        Assert.Equal("sftp:backup@nas.example.com:/volume1/kaimo", url);
    }

    [Fact]
    public void KitCode_IsSixBase32Characters_AndMatchesLeniently()
    {
        var password = ResticBackend.GeneratePassword();
        var code = ResticBackend.KitCode(password);

        Assert.Matches("^[A-Z2-7]{6}$", code);
        Assert.True(ResticBackend.KitCodeMatches(password, " " + code.ToLowerInvariant()[..3] + "-" + code[3..] + " "));
        var wrong = (code[0] == 'A' ? 'B' : 'A') + code[1..];
        Assert.False(ResticBackend.KitCodeMatches(password, wrong));
        Assert.False(ResticBackend.KitCodeMatches(password, ""));
        Assert.NotEqual(ResticBackend.GeneratePassword(), ResticBackend.GeneratePassword());
    }

    // ─────────────── restic CLI ───────────────

    private sealed class FakeRunner(int exitCode, IReadOnlyList<string>? stdout = null, string stderr = "") : IProcessRunner
    {
        public List<string> Arguments { get; } = [];
        public IReadOnlyDictionary<string, string>? Environment { get; private set; }
        public bool ClearedEnvironment { get; private set; }

        public Task<ProcessResult> RunAsync(string fileName, IReadOnlyList<string> arguments,
            IReadOnlyDictionary<string, string>? environment = null, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<ProcessResult> RunStreamingAsync(string fileName, IReadOnlyList<string> arguments,
            IReadOnlyDictionary<string, string>? environment, Action<string>? onStdoutLine, bool clearEnvironment,
            CancellationToken cancellationToken = default)
        {
            Arguments.AddRange(arguments);
            Environment = environment;
            ClearedEnvironment = clearEnvironment;
            foreach (var line in stdout ?? [])
                onStdoutLine?.Invoke(line);
            return Task.FromResult(new ProcessResult(exitCode, onStdoutLine is null ? string.Join('\n', stdout ?? []) : "", stderr));
        }

        public byte[] DumpContent { get; init; } = [];

        public async Task<ProcessResult> RunToStreamAsync(string fileName, IReadOnlyList<string> arguments,
            IReadOnlyDictionary<string, string>? environment, Stream stdoutTarget, bool clearEnvironment,
            CancellationToken cancellationToken = default)
        {
            Arguments.AddRange(arguments);
            Environment = environment;
            ClearedEnvironment = clearEnvironment;
            await stdoutTarget.WriteAsync(DumpContent, cancellationToken);
            return new ProcessResult(exitCode, "", stderr);
        }
    }

    private static ResticTarget Target()
        => new("/repo", new Dictionary<string, string> { ["RESTIC_PASSWORD"] = "pw-secret", ["RESTIC_REPOSITORY"] = "/repo" },
            ["-o", "s3.bucket-lookup=path"], null);

    private static ResticClient Client(IProcessRunner runner)
        => new(runner, new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Backup:Restic:CacheDirectory"] = "/cache",
        }).Build());

    private static readonly ResticBackupRequest Request = new(
        ["/data/storage/pool01/projects", "/meta/share-1/kaimo-acl-manifest.json"],
        ["kaimo", "share:abc"],
        ["/data/storage/pool01/projects/.kaimo-*"]);

    [Fact]
    public async Task Backup_BuildsArguments_WithFixedHostAndPathsAfterSeparator()
    {
        var runner = new FakeRunner(0, ["""{"message_type":"summary","files_new":3,"files_changed":1,"files_unmodified":7,"data_added":1024,"total_bytes_processed":4096,"snapshot_id":"abc123"}"""]);
        var result = await Client(runner).BackupAsync(Target(), Request, null, CancellationToken.None);

        var args = runner.Arguments;
        Assert.Contains("backup", args);
        Assert.Equal("kaimo", args[args.IndexOf("--host") + 1]);
        Assert.Contains("--json", args);
        Assert.Equal("/cache", args[args.IndexOf("--cache-dir") + 1]);
        Assert.Equal("30m", args[args.IndexOf("--retry-lock") + 1]);
        Assert.Contains("s3.bucket-lookup=path", args);
        // Nothing but the paths may follow the separator — a global flag there would be
        // backed up as a path (regression found against the real binary).
        var separator = args.IndexOf("--");
        Assert.Equal(Request.Paths, args.Skip(separator + 1));
        Assert.True(args.IndexOf("--json") < args.IndexOf("backup"));
        Assert.True(args.IndexOf("--exclude") < separator);
        Assert.DoesNotContain(args, a => a.Contains("pw-secret"));
        Assert.True(runner.ClearedEnvironment);
        Assert.Equal("pw-secret", runner.Environment!["RESTIC_PASSWORD"]);

        Assert.Equal("abc123", result.SnapshotId);
        Assert.Equal(3, result.FilesNew);
        Assert.Equal(1024, result.BytesAdded);
        Assert.False(result.HasWarnings);
    }

    [Fact]
    public async Task Backup_ExitCode3_IsSuccessWithWarnings()
    {
        var runner = new FakeRunner(3,
        [
            """{"message_type":"status","percent_done":0.5}""",
            """{"message_type":"error","error":{"message":"permission denied"},"during":"archival","item":"/data/x/locked.docx"}""",
            """{"message_type":"summary","snapshot_id":"def456","files_new":1}""",
            "not json at all",
        ]);
        var progress = new List<double>();
        var result = await Client(runner).BackupAsync(Target(), Request, p => progress.Add(p.PercentDone), CancellationToken.None);

        Assert.True(result.HasWarnings);
        Assert.Equal(1, result.WarningCount);
        Assert.Contains("/data/x/locked.docx: permission denied", result.Warnings);
        Assert.Equal("def456", result.SnapshotId);
        Assert.Equal([0.5], progress);
    }

    [Theory]
    [InlineData(10, "", "repo_not_found")]
    [InlineData(11, "", "lock_failed")]
    [InlineData(12, "", "wrong_password")]
    [InlineData(130, "", "cancelled")]
    [InlineData(1, "Fatal: create key in repository at /r failed: repository master key and config already initialized", "repo_exists")]
    [InlineData(1, "Fatal: unable to open repository: dial tcp: lookup nas: no such host", "repo_unreachable")]
    [InlineData(1, "something else", "restic_failed")]
    public async Task Failures_MapToStableCodes(int exitCode, string stderr, string expected)
    {
        var runner = new FakeRunner(exitCode, [], stderr);
        var ex = await Assert.ThrowsAsync<ResticException>(() => Client(runner).CatConfigAsync(Target(), CancellationToken.None));
        Assert.Equal(expected, ex.Code);
        Assert.Equal(expected, ResticClient.MapErrorCode(exitCode, stderr));
    }

    [Fact]
    public async Task JsonExitError_OnStdout_IsUsedForErrorMapping()
    {
        // Real restic 0.19 output of a second "init" in --json mode.
        var runner = new FakeRunner(1, ["""{"message_type":"exit_error","code":1,"message":"Fatal: create repository at /r failed: config file already exists"}"""]);
        var ex = await Assert.ThrowsAsync<ResticException>(() => Client(runner).InitAsync(Target(), CancellationToken.None));
        Assert.Equal("repo_exists", ex.Code);
        Assert.Contains("config file already exists", (string)ex.Data["stderr"]!);
    }

    [Fact]
    public async Task CatConfig_ReturnsRepositoryId()
    {
        var runner = new FakeRunner(0, ["{", "  \"version\": 2,", "  \"id\": \"5f1d0c\",", "  \"chunker_polynomial\": \"3dea92648f6e83\"", "}"]);
        Assert.Equal("5f1d0c", await Client(runner).CatConfigAsync(Target(), CancellationToken.None));
        Assert.DoesNotContain("--retry-lock", runner.Arguments);
    }

    [Fact]
    public void ParseSnapshots_ReadsTagsPathsAndSummary_Tolerantly()
    {
        var list = ResticClient.ParseSnapshots("""
            [{"time":"2026-10-05T02:00:03.123+02:00","paths":["/data/p"],"hostname":"kaimo","tags":["kaimo","share:abc"],
              "id":"aaa","short_id":"aaa","summary":{"total_files_processed":12,"total_bytes_processed":3400},"unknown":1},
             {"id":"bbb","time":"garbage"}, {"no_id":true}]
            """);
        Assert.Equal(2, list.Count);
        Assert.Equal(new DateTime(2026, 10, 5, 0, 0, 3, 123, DateTimeKind.Utc), list[0].TimeUtc);
        Assert.Equal(12, list[0].FileCount);
        Assert.Equal(["kaimo", "share:abc"], list[0].Tags);
        Assert.Empty(list[1].Tags);
    }

    // ─────────────── Restore: restic CLI ───────────────

    [Fact]
    public void RestoreArgs_UseTheFolderAsRoot_AndEscapeNamesAsLiteralIncludes()
    {
        var args = ResticClient.BuildRestoreArgs("abc123", "/data/storage/pool01/projects/docs/", ["a*b?.txt", "[x]", "plain"], "/stage/data");

        Assert.Equal("/stage/data", args[args.IndexOf("--target") + 1]);
        Assert.Contains("--verify", args);
        Assert.Equal(["/a\\*b\\?.txt", "/\\[x]", "/plain"],
            args.Select((a, i) => (a, i)).Where(x => x.i > 0 && args[x.i - 1] == "--include").Select(x => x.a));
        // Only the snapshot reference follows the separator, so no name can be read as a flag.
        Assert.Equal(["abc123:/data/storage/pool01/projects/docs"], args.Skip(args.IndexOf("--") + 1));
        Assert.DoesNotContain("--include", ResticClient.BuildRestoreArgs("s", "/p", [], "/t"));
    }

    [Theory]
    [InlineData("a/b")]
    [InlineData("..")]
    [InlineData("")]
    public void RestoreArgs_RejectNamesOutsideTheFolder(string name)
        => Assert.Equal("path_invalid",
            Assert.Throws<ResticException>(() => ResticClient.BuildRestoreArgs("s", "/p", [name], "/t")).Code);

    [Fact]
    public async Task ListDirectory_ReturnsChildrenOnly_AndToleratesOtherLines()
    {
        var runner = new FakeRunner(0,
        [
            """{"time":"2026-10-05T02:00:03Z","paths":["/data/p"],"id":"aaa","message_type":"snapshot","struct_type":"snapshot"}""",
            """{"name":"docs","type":"dir","path":"/data/p/docs","mtime":"2026-10-01T10:00:00+02:00","message_type":"node","struct_type":"node"}""",
            """{"name":"a.txt","type":"file","path":"/data/p/docs/a.txt","size":42,"mtime":"2026-10-02T10:00:00Z","struct_type":"node"}""",
            """{"name":"sub","type":"dir","path":"/data/p/docs/sub","message_type":"node"}""",
            "garbage",
        ]);

        var nodes = await Client(runner).ListDirectoryAsync(Target(), "aaa", "/data/p/docs/", CancellationToken.None);

        Assert.Equal(["a.txt", "sub"], nodes.Select(n => n.Name));
        Assert.Equal(42, nodes[0].Size);
        Assert.False(nodes[0].IsDirectory);
        Assert.True(nodes[1].IsDirectory);
        Assert.Equal(new DateTime(2026, 10, 2, 10, 0, 0, DateTimeKind.Utc), nodes[0].ModifiedUtc);
        Assert.Equal(["--", "aaa", "/data/p/docs"], runner.Arguments.Skip(runner.Arguments.IndexOf("ls") + 1));
        Assert.Equal("10s", runner.Arguments[runner.Arguments.IndexOf("--retry-lock") + 1]);
    }

    [Fact]
    public async Task Restore_ReadsSummary_AndFailsOnErrors()
    {
        var ok = new FakeRunner(0,
        [
            """{"message_type":"status","percent_done":0.4}""",
            """{"message_type":"summary","total_files":3,"files_restored":3,"bytes_restored":300}""",
        ]);
        var progress = new List<double>();
        var result = await Client(ok).RestoreAsync(Target(), "s", "/p", ["a"], "/t", progress.Add, CancellationToken.None);
        Assert.Equal(3, result.FilesRestored);
        Assert.Equal(300, result.BytesRestored);
        Assert.Equal([0.4], progress);

        var failing = new FakeRunner(1, ["""{"message_type":"error","error":{"message":"verification failed"},"during":"restore","item":"/p/a"}"""]);
        var ex = await Assert.ThrowsAsync<ResticException>(() =>
            Client(failing).RestoreAsync(Target(), "s", "/p", ["a"], "/t", null, CancellationToken.None));
        Assert.Contains("/p/a: verification failed", (string)ex.Data["stderr"]!);
    }

    [Fact]
    public async Task Dump_StreamsRawContent_WithoutJson_AndZipsFolders()
    {
        var runner = new FakeRunner(0) { DumpContent = [1, 2, 3] };
        using var output = new MemoryStream();

        await Client(runner).DumpAsync(Target(), "s1", "/data/p/docs", asZipArchive: true, output, CancellationToken.None);

        Assert.Equal([1, 2, 3], output.ToArray());
        Assert.DoesNotContain("--json", runner.Arguments);
        Assert.Equal("zip", runner.Arguments[runner.Arguments.IndexOf("--archive") + 1]);
        // The parent becomes the archive root: entries are "docs/…", never the server path.
        Assert.Equal(["s1:/data/p", "/docs"], runner.Arguments.Skip(runner.Arguments.IndexOf("--") + 1));
        Assert.True(runner.ClearedEnvironment);
    }

    [Fact]
    public async Task Dump_Failure_MapsTheExitCode()
    {
        var runner = new FakeRunner(12);
        var ex = await Assert.ThrowsAsync<ResticException>(() =>
            Client(runner).DumpAsync(Target(), "s1", "/x", false, Stream.Null, CancellationToken.None));
        Assert.Equal("wrong_password", ex.Code);
    }

    // ─────────────── Restore: placement ───────────────

    [Fact]
    public void SourceRoot_IsThePathThatIsNotTheManifest()
    {
        var snapshot = new BackupSnapshot
        {
            PathsJson = """["/data/storage/pool01/projects","/data/kaimo-backups/restic-meta/share-1/kaimo-acl-manifest.json"]""",
        };
        Assert.Equal("/data/storage/pool01/projects", BackupRestoreExecutor.SourceRoot(snapshot));
        Assert.EndsWith("/kaimo-acl-manifest.json", BackupRestoreExecutor.ManifestPath(snapshot));
        Assert.Equal("/data/storage/pool01/projects/docs/a", BackupRestoreExecutor.SnapshotPath("/data/storage/pool01/projects/", "docs/a"));
        Assert.Equal("/data/storage/pool01/projects", BackupRestoreExecutor.SnapshotPath("/data/storage/pool01/projects", ""));
    }

    [Fact]
    public void MapPath_MapsTheSelectionOnly_AndLeavesUntouchedPathsAlone()
    {
        var items = new HashSet<string> { "a", "b.txt" };
        Assert.Equal("Restored/a/x.txt", BackupRestoreExecutor.MapPath("docs/a/x.txt", "docs", items, "Restored", []));
        Assert.Equal("docs/b.txt", BackupRestoreExecutor.MapPath("docs/b.txt", "docs", items, "docs", []));
        Assert.Null(BackupRestoreExecutor.MapPath("docs/c.txt", "docs", items, "docs", []));
        Assert.Null(BackupRestoreExecutor.MapPath("docs", "docs", items, "docs", []));
        Assert.Null(BackupRestoreExecutor.MapPath("docsX/a", "docs", items, "docs", []));
        Assert.Null(BackupRestoreExecutor.MapPath("docs/a/x.txt", "docs", items, "docs", ["docs/a"]));

        // Whole share root: everything but the root row itself.
        var all = new HashSet<string>();
        Assert.Equal("x/y", BackupRestoreExecutor.MapPath("x/y", "", all, "", []));
        Assert.Null(BackupRestoreExecutor.MapPath("", "", all, "", []));
    }

    [Fact]
    public void RestoredFolderName_IsNumberedWhenTaken()
    {
        var root = Directory.CreateTempSubdirectory("kaimo-restore-name-").FullName;
        try
        {
            var now = new DateTime(2026, 10, 10, 12, 0, 0, DateTimeKind.Utc);
            var first = BackupRestoreExecutor.UniqueRestoredFolderName(root, "", now, System.Globalization.CultureInfo.InvariantCulture);
            Directory.CreateDirectory(Path.Combine(root, first));
            var second = BackupRestoreExecutor.UniqueRestoredFolderName(root, "", now, System.Globalization.CultureInfo.InvariantCulture);

            Assert.Matches(@"\d{4}-\d{2}-\d{2} \d{4}$", first);
            Assert.Equal(first + " (2)", second);
            // Named in the requesting admin's language, whatever culture the runner thread has.
            Assert.StartsWith("Wiederhergestellt ", BackupRestoreExecutor.UniqueRestoredFolderName(root, "", now,
                System.Globalization.CultureInfo.GetCultureInfo("de-DE")));
            Assert.StartsWith("Restored ", BackupRestoreExecutor.UniqueRestoredFolderName(root, "", now,
                System.Globalization.CultureInfo.GetCultureInfo("en-US")));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    /// <summary>Records which file contents were kept as a version before being overwritten.</summary>
    private sealed class CountingVersions
    {
        private readonly Moq.Mock<Kaimo_File_Server.Core.Services.File.IFileVersionService> _mock = new();

        public CountingVersions()
            => _mock.Setup(v => v.CreateVersionAsync(Moq.It.IsAny<Guid>(), Moq.It.IsAny<string>(), Moq.It.IsAny<Stream>(), Moq.It.IsAny<string?>()))
                .Returns<Guid, string, Stream, string?>(async (_, path, content, _) =>
                {
                    using var reader = new StreamReader(content, leaveOpen: true);
                    var text = await reader.ReadToEndAsync();
                    Created.Add((path, text));
                    return new FileVersion(Guid.NewGuid(), path, DateTime.UtcNow, "hash", "blob", text.Length, null, 1);
                });

        public List<(string Path, string Content)> Created { get; } = [];

        public Kaimo_File_Server.Core.Services.File.IFileVersionService Service => _mock.Object;
    }

    /// <summary>Staging with docs/{a.txt,new.txt,sub/b.txt}; share with docs/{a.txt,extra.txt,sub/b.txt}.</summary>
    private static (string Root, string Staged, string Share) MergeFixture()
    {
        var root = Directory.CreateTempSubdirectory("kaimo-restore-merge-").FullName;
        var staged = Path.Combine(root, "stage");
        var share = Path.Combine(root, "share");
        Directory.CreateDirectory(Path.Combine(staged, "docs", "sub"));
        File.WriteAllText(Path.Combine(staged, "docs", "a.txt"), "backup-a");
        File.WriteAllText(Path.Combine(staged, "docs", "new.txt"), "backup-new");
        File.WriteAllText(Path.Combine(staged, "docs", "sub", "b.txt"), "backup-b");
        Directory.CreateDirectory(Path.Combine(share, "docs", "sub"));
        File.WriteAllText(Path.Combine(share, "docs", "a.txt"), "live-a");
        File.WriteAllText(Path.Combine(share, "docs", "extra.txt"), "live-extra");
        File.WriteAllText(Path.Combine(share, "docs", "sub", "b.txt"), "live-b");
        return (root, staged, share);
    }

    private static string Read(string share, string relative) => File.ReadAllText(Path.Combine(share, relative.Replace('/', Path.DirectorySeparatorChar)));

    [Fact]
    public async Task Merge_Overwrite_KeepsAVersion_RecreatesMissing_AndNeverDeletes()
    {
        var (root, staged, share) = MergeFixture();
        try
        {
            var versions = new CountingVersions();
            var outcome = new BackupRestoreOutcome();
            var merge = new RestoreMerge(share, Guid.NewGuid(), BackupRestoreConflict.Overwrite, versions.Service, "u1", " (restored 2026-10-10)", outcome);

            await merge.MoveAsync(Path.Combine(staged, "docs"), "docs");

            Assert.Equal("backup-a", Read(share, "docs/a.txt"));
            Assert.Equal("backup-new", Read(share, "docs/new.txt"));
            Assert.Equal("backup-b", Read(share, "docs/sub/b.txt"));
            Assert.Equal("live-extra", Read(share, "docs/extra.txt"));
            Assert.Equal([("docs/a.txt", "live-a"), ("docs/sub/b.txt", "live-b")], versions.Created.OrderBy(v => v.Path));
            Assert.Equal(2, outcome.Overwritten);
            Assert.Equal(2, outcome.VersionsCreated);
            Assert.Empty(merge.Untouched);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Theory]
    [InlineData(BackupRestoreConflict.Overwrite)]
    [InlineData(BackupRestoreConflict.Rename)]
    public async Task Merge_IdenticalFile_IsLeftAlone_WithoutVersionOrCopy(BackupRestoreConflict conflict)
    {
        var (root, staged, share) = MergeFixture();
        try
        {
            File.WriteAllText(Path.Combine(staged, "docs", "same.txt"), "identical");
            File.WriteAllText(Path.Combine(share, "docs", "same.txt"), "identical");
            var versions = new CountingVersions();
            var outcome = new BackupRestoreOutcome();
            var merge = new RestoreMerge(share, Guid.NewGuid(), conflict, versions.Service, null, " (r)", outcome);

            await merge.MoveAsync(Path.Combine(staged, "docs", "same.txt"), "docs/same.txt");

            Assert.Equal(1, outcome.Unchanged);
            Assert.Empty(versions.Created);
            Assert.False(File.Exists(Path.Combine(share, "docs", "same (r).txt")));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Merge_Skip_LeavesExistingFilesAndRecordsThem()
    {
        var (root, staged, share) = MergeFixture();
        try
        {
            var outcome = new BackupRestoreOutcome();
            var merge = new RestoreMerge(share, Guid.NewGuid(), BackupRestoreConflict.Skip, new CountingVersions().Service, null, " (r)", outcome);

            await merge.MoveAsync(Path.Combine(staged, "docs"), "docs");

            Assert.Equal("live-a", Read(share, "docs/a.txt"));
            Assert.Equal("backup-new", Read(share, "docs/new.txt"));
            Assert.Equal(2, outcome.Skipped);
            Assert.Equal(["docs/a.txt", "docs/sub/b.txt"], merge.Untouched.Order());
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Merge_Rename_RestoresBesideTheExistingFile_Numbered()
    {
        var (root, staged, share) = MergeFixture();
        try
        {
            File.WriteAllText(Path.Combine(share, "docs", "a (restored 2026-10-10).txt"), "earlier restore");
            var outcome = new BackupRestoreOutcome();
            var merge = new RestoreMerge(share, Guid.NewGuid(), BackupRestoreConflict.Rename, new CountingVersions().Service, null, " (restored 2026-10-10)", outcome);

            await merge.MoveAsync(Path.Combine(staged, "docs"), "docs");

            Assert.Equal("live-a", Read(share, "docs/a.txt"));
            Assert.Equal("backup-a", Read(share, "docs/a (restored 2026-10-10) (2).txt"));
            Assert.Equal("backup-b", Read(share, "docs/sub/b (restored 2026-10-10).txt"));
            Assert.Equal(2, outcome.Renamed);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Merge_FolderMeetingAFile_IsRestoredBesideIt_EvenWhenOverwriting()
    {
        var (root, staged, share) = MergeFixture();
        try
        {
            Directory.Delete(Path.Combine(share, "docs", "sub"), recursive: true);
            File.WriteAllText(Path.Combine(share, "docs", "sub"), "a file named sub");
            var outcome = new BackupRestoreOutcome();
            var merge = new RestoreMerge(share, Guid.NewGuid(), BackupRestoreConflict.Overwrite, new CountingVersions().Service, null, " (r)", outcome);

            await merge.MoveAsync(Path.Combine(staged, "docs", "sub"), "docs/sub");

            Assert.Equal("a file named sub", Read(share, "docs/sub"));
            Assert.Equal("backup-b", Read(share, "docs/sub (r)/b.txt"));
            Assert.Equal(1, outcome.Renamed);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

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

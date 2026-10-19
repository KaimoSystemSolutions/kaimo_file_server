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
}

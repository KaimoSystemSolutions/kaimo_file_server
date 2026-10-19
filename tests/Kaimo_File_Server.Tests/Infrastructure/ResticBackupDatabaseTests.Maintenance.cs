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
    /// <summary>Answers each restic command with a canned result and records the call order.</summary>
    private sealed class ScriptedRunner(Func<string, (int Exit, string[] Stdout)> script) : IProcessRunner
    {
        public List<List<string>> Calls { get; } = [];

        public Task<ProcessResult> RunAsync(string fileName, IReadOnlyList<string> arguments,
            IReadOnlyDictionary<string, string>? environment = null, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<ProcessResult> RunStreamingAsync(string fileName, IReadOnlyList<string> arguments,
            IReadOnlyDictionary<string, string>? environment, Action<string>? onStdoutLine, bool clearEnvironment,
            CancellationToken cancellationToken = default)
        {
            Calls.Add(arguments.ToList());
            var command = arguments.First(a => a is "unlock" or "backup" or "forget" or "prune" or "check" or "snapshots" or "copy" or "ls" or "restore");
            var (exit, stdout) = script(command);
            foreach (var line in stdout)
                onStdoutLine?.Invoke(line);
            return Task.FromResult(new ProcessResult(exit, "", ""));
        }

        /// <summary><c>restic dump</c>: writes a fixed payload.</summary>
        public async Task<ProcessResult> RunToStreamAsync(string fileName, IReadOnlyList<string> arguments,
            IReadOnlyDictionary<string, string>? environment, Stream stdoutTarget, bool clearEnvironment,
            CancellationToken cancellationToken = default)
        {
            Calls.Add(arguments.ToList());
            await stdoutTarget.WriteAsync("PGDMP"u8.ToArray(), cancellationToken);
            return new ProcessResult(0, "", "");
        }
    }

    private sealed record MaintenanceFixture(
        BackupMaintenanceExecutor Executor, BackupJobExecutor Jobs, List<Core.Domain.Notifications.NotificationEvent> Published,
        BackupRepository Repo, string Root);

    /// <summary>pg_dump stand-in of the fixtures: writes a file, or throws when set.</summary>
    private readonly Mock<IDatabaseBackupService> _database = new();

    private async Task<MaintenanceFixture> MaintenanceFixtureAsync(ScriptedRunner runner, bool appendOnly)
    {
        var root = Path.Combine(Path.GetTempPath(), "kaimo-maintenance-" + Guid.NewGuid().ToString("N"));
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Backup:Restic:LocalRoots"] = root,
            ["Backup:Restic:CacheDirectory"] = Path.Combine(root, "cache"),
            ["Backup:Restic:TempDirectory"] = Path.Combine(root, "tmp"),
            ["Backup:Restic:MetaDirectory"] = Path.Combine(root, "meta"),
            ["Storage:RootPath"] = Path.Combine(root, "storage"),
            ["Storage:ApplicationDataPath"] = Path.Combine(root, "appdata"),
        }).Build();
        var vault = new Mock<ICredentialVault>();
        vault.Setup(v => v.Unprotect<string>(It.IsAny<string>(), It.IsAny<CredentialContext>())).Returns("pw");
        var published = new List<Core.Domain.Notifications.NotificationEvent>();
        var notifications = new Mock<Core.Services.Notifications.INotificationPublisher>();
        notifications.Setup(n => n.PublishAsync(It.IsAny<Core.Domain.Notifications.NotificationEvent>(), It.IsAny<CancellationToken>()))
            .Callback<Core.Domain.Notifications.NotificationEvent, CancellationToken>((e, _) => published.Add(e))
            .Returns(Task.CompletedTask);

        var repo = new BackupRepository
        {
            Name = "NAS",
            Backend = BackupBackend.Local,
            State = BackupRepositoryState.Active,
            SettingsJson = new ResticRepositorySettings { Path = Path.Combine(root, "repo") }.Serialize(),
            EncryptedPassword = "protected",
            IsAppendOnly = appendOnly,
            TestRestoreSampleSize = 0,
        };
        await using (var db = NewContext())
        {
            db.BackupRepositories.Add(repo);
            db.BackupJobs.Add(new BackupJob { Name = "Nightly", RepositoryId = repo.Id, Retention = new BackupRetention { KeepLast = 1 } });
            db.BackupJobs.Add(new BackupJob { Name = "Keep all", RepositoryId = repo.Id });
            await db.SaveChangesAsync();
        }

        var resolver = new ResticTargetResolver(DbFactory, vault.Object, config);
        var restic = new ResticClient(runner, config);
        var jobs = new BackupJobExecutor(DbFactory, resolver, restic, config, TimeProvider.System, notifications.Object, _database.Object,
            NullLogger<BackupJobExecutor>.Instance);
        var executor = new BackupMaintenanceExecutor(DbFactory, resolver, restic, jobs, TimeProvider.System, notifications.Object,
            _database.Object, NullLogger<BackupMaintenanceExecutor>.Instance);
        return new MaintenanceFixture(executor, jobs, published, repo, root);
    }

    [Fact]
    public async Task Backup_AppliesTheJobRetentionRightAfterwards_AsItsOwnRun()
    {
        var runner = new ScriptedRunner(command => command switch
        {
            "backup" => (0, ["""{"message_type":"summary","snapshot_id":"s3","files_new":1}"""]),
            "forget" => (0, ["""[{"host":"kaimo","paths":["/a"],"keep":[{"id":"s3"}],"remove":[{"id":"s1"}]}]"""]),
            "snapshots" => (0, ["[]"]),
            _ => (0, []),
        });
        var fixture = await MaintenanceFixtureAsync(runner, appendOnly: false);
        var shareDir = Path.Combine(fixture.Root, "share");
        Directory.CreateDirectory(shareDir);
        Directory.CreateDirectory(Path.Combine(fixture.Root, "repo")); // free-space guard of local repositories
        await File.WriteAllTextAsync(Path.Combine(shareDir, "a.txt"), "a");
        Guid jobId, runId;
        await using (var db = NewContext())
        {
            var share = new ShareDefinition("docs", shareDir);
            db.ShareDefinitions.Add(share);
            var job = await db.BackupJobs.SingleAsync(j => j.Name == "Nightly");
            db.BackupJobSources.Add(new BackupJobSource { JobId = job.Id, ShareId = share.Id });
            var run = new BackupRun { RepositoryId = fixture.Repo.Id, JobId = job.Id, Type = BackupRunType.Backup };
            db.BackupRuns.Add(run);
            await db.SaveChangesAsync();
            (jobId, runId) = (job.Id, run.Id);
        }

        await fixture.Jobs.ExecuteAsync(runId, (_, _) => { }, CancellationToken.None);

        await using var check = NewContext();
        var runs = await check.BackupRuns.OrderBy(r => r.QueuedAtUtc).ToListAsync();
        Assert.Equal([(BackupRunType.Backup, BackupRunStatus.Succeeded, (string?)null), (BackupRunType.Forget, BackupRunStatus.Succeeded, null)],
            runs.Select(r => (r.Type, r.Status, r.ErrorCode)));
        var forget = runner.Calls.Single(c => c.Contains("forget"));
        Assert.Equal("job:" + jobId.ToString("N"), forget[forget.IndexOf("--tag") + 1]);
        Assert.DoesNotContain(runner.Calls, c => c.Contains("prune"));
        Assert.Equal(jobId, runs[1].JobId);
        Assert.Equal(1, BackupMaintenanceDetails.TryParse(runs[1].DetailsJson)!.SnapshotsRemoved);
    }

    [Fact]
    public async Task Maintenance_PrunesEvenWhenItsOwnRetentionPassRemovedNothing()
    {
        var runner = new ScriptedRunner(command => command switch
        {
            "forget" => (0, ["[]"]),
            "snapshots" => (0, ["[]"]),
            _ => (0, []),
        });
        var fixture = await MaintenanceFixtureAsync(runner, appendOnly: false);

        await fixture.Executor.ExecuteAsync(fixture.Repo.Id, BackupRunTrigger.Scheduled, null, (_, _) => { }, CancellationToken.None);

        Assert.Contains(runner.Calls, c => c.Contains("prune"));
    }

    [Fact]
    public async Task Maintenance_ForgetsPrunesAndChecks_EachAsItsOwnRun_AndReportsDamage()
    {
        var runner = new ScriptedRunner(command => command switch
        {
            "forget" => (0, ["""[{"host":"kaimo","paths":["/a"],"keep":[{"id":"1"}],"remove":[{"id":"2"},{"id":"3"}]}]"""]),
            "snapshots" => (0, ["[]"]),
            "check" => (1, ["""{"message_type":"summary","num_errors":1}"""]),
            _ => (0, []),
        });
        var (executor, _, published, repo, _) = await MaintenanceFixtureAsync(runner, appendOnly: false);

        await executor.ExecuteAsync(repo.Id, BackupRunTrigger.Manual, null, (_, _) => { }, CancellationToken.None);

        // Stale locks first; only the job with a retention is forgotten; prune because points went away.
        Assert.Equal(["unlock", "forget", "snapshots", "prune", "check"],
            runner.Calls.Select(c => c.First(a => a is "unlock" or "forget" or "snapshots" or "prune" or "check")));
        Assert.Contains("--read-data-subset", runner.Calls.Last());

        await using var db = NewContext();
        var runs = await db.BackupRuns.OrderBy(r => r.QueuedAtUtc).ToListAsync();
        Assert.Equal([BackupRunType.Forget, BackupRunType.Prune, BackupRunType.Check], runs.Select(r => r.Type));
        Assert.Equal(2, BackupMaintenanceDetails.TryParse(runs[0].DetailsJson)!.SnapshotsRemoved);
        Assert.Equal(BackupRunStatus.Succeeded, runs[1].Status);
        Assert.Equal(BackupRunStatus.Failed, runs[2].Status);
        Assert.Equal("check_failed", runs[2].ErrorCode);
        var stored = await db.BackupRepositories.SingleAsync(r => r.Id == repo.Id);
        Assert.False(stored.LastCheckOk);
        Assert.NotNull(stored.LastReadDataAtUtc);
        Assert.Contains(published, e => e.Type == Core.Services.Notifications.NotificationEventCatalog.FileBackupCheckFailed);
    }

    [Fact]
    public async Task Maintenance_AppendOnly_SkipsForgetAndPrune()
    {
        var runner = new ScriptedRunner(_ => (0, []));
        var (executor, _, published, repo, _) = await MaintenanceFixtureAsync(runner, appendOnly: true);

        await executor.ExecuteAsync(repo.Id, BackupRunTrigger.Scheduled, null, (_, _) => { }, CancellationToken.None);

        Assert.DoesNotContain(runner.Calls, c => c.Contains("forget") || c.Contains("prune"));
        await using var db = NewContext();
        var run = await db.BackupRuns.SingleAsync();
        Assert.Equal((BackupRunType.Check, BackupRunStatus.Succeeded, BackupRunTrigger.Scheduled), (run.Type, run.Status, run.Trigger));
        Assert.True((await db.BackupRepositories.SingleAsync(r => r.Id == repo.Id)).LastCheckOk);
        Assert.Empty(published);
    }

    [Fact]
    public async Task AclManifest_ContainsOwnersAndEntriesWithPrincipalNames()
    {
        var share = new ShareDefinition("projects", "/data/storage/pool01/projects");
        var group = new Group(Guid.NewGuid(), "Engineers");
        await using (var db = NewContext())
        {
            db.Users.Add(_user);
            db.Groups.Add(group);
            db.ShareDefinitions.Add(share);
            db.FileMetadata.Add(new FileMetadata
            {
                Id = Guid.NewGuid(), ShareId = share.Id, Path = "docs", Name = "docs", IsDirectory = true, OwnerId = _user.Id,
                Acl = [new AccessEntry(group.Id, AclEntryType.Allow, FilePermission.TakeOwnership, AclInheritance.ThisFolder)],
            });
            await db.SaveChangesAsync();
        }

        var path = Path.Combine(Path.GetTempPath(), "kaimo-manifest-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            await using (var db = NewContext())
                await BackupAclManifest.WriteAsync(db, share, path, DateTime.UtcNow, CancellationToken.None);

            using var doc = JsonDocument.Parse(await File.ReadAllTextAsync(path));
            var root = doc.RootElement;
            Assert.Equal(BackupAclManifest.FormatVersion, root.GetProperty("formatVersion").GetInt32());
            Assert.Equal(share.Id, root.GetProperty("share").GetProperty("id").GetGuid());
            var entry = Assert.Single(root.GetProperty("entries").EnumerateArray());
            Assert.Equal("docs", entry.GetProperty("path").GetString());
            Assert.Equal("alice", entry.GetProperty("ownerName").GetString());
            var ace = Assert.Single(entry.GetProperty("acl").EnumerateArray());
            Assert.Equal("Engineers", ace.GetProperty("principalName").GetString());
            Assert.Equal((long)FilePermission.TakeOwnership, ace.GetProperty("permissions").GetInt64());
        }
        finally
        {
            File.Delete(path);
        }
    }
}

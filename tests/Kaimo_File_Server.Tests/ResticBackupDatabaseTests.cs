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

/// <summary>
/// Backup catalog authorization and the ACL manifest against the real EF schema (Sqlite).
/// </summary>
public class ResticBackupDatabaseTests : DatabaseTestBase
{
    private readonly User _user = new(Guid.NewGuid(), "Alice", "alice", "hash", "nt");
    private readonly Mock<IManagementAuthService> _auth = new();

    private UserContext Actor => new(_user, [], [], []);

    public ResticBackupDatabaseTests()
    {
        // No restore rights unless a test grants them.
        _auth.Setup(a => a.GetAuthorizedShareIdsAnyAsync(It.IsAny<UserContext>(), It.IsAny<ManagementPermission>()))
            .ReturnsAsync(AuthorizedScopeResult.LimitedTo([]));
        _auth.Setup(a => a.GetAuthorizedShareIdsAsync(It.IsAny<UserContext>(), It.IsAny<ManagementPermission>()))
            .ReturnsAsync(AuthorizedScopeResult.LimitedTo([]));
    }

    private BackupCatalogService Catalog(bool demo = false, ICredentialVault? vault = null, IConfiguration? config = null)
    {
        config ??= new ConfigurationBuilder().Build();
        return new BackupCatalogService(
            DbFactory,
            _auth.Object,
            new ResticTargetResolver(DbFactory, vault ?? Mock.Of<ICredentialVault>(), config),
            new ResticClient(Mock.Of<IProcessRunner>(), config),
            Mock.Of<IBackupRunner>(),
            new DemoModeOptions { ReadOnly = demo },
            TimeProvider.System,
            NullLogger<BackupCatalogService>.Instance);
    }

    /// <summary>A department-scoped job admin for the given shares, no repository rights.</summary>
    private void ScopedJobAdmin(params Guid[] shareIds)
    {
        _auth.Setup(a => a.HasGlobalPermissionAsync(It.IsAny<UserContext>(), It.IsAny<ManagementPermission>())).ReturnsAsync(false);
        _auth.Setup(a => a.GetAuthorizedShareIdsAsync(It.IsAny<UserContext>(), ManagementPermission.ManageBackupJobs))
            .ReturnsAsync(AuthorizedScopeResult.LimitedTo(shareIds));
    }

    private async Task<(Department Parent, Department Child, ShareDefinition ChildShare, ShareDefinition OtherShare, BackupRepository Repo)> SeedAsync(
        bool releaseToParent)
    {
        await using var db = NewContext();
        var parent = new Department("Engineering");
        var child = new Department("Backend", parentDepartmentId: parent.Id);
        var other = new Department("Sales");
        db.Departments.AddRange(parent, child, other);
        var childShare = new ShareDefinition("backend", "/data/storage/pool01/backend", child.Id);
        var otherShare = new ShareDefinition("sales", "/data/storage/pool01/sales", other.Id);
        db.ShareDefinitions.AddRange(childShare, otherShare);
        var repo = new BackupRepository { Name = "NAS", Backend = BackupBackend.Local, State = BackupRepositoryState.Active };
        db.BackupRepositories.Add(repo);
        if (releaseToParent)
            db.BackupRepositoryDepartments.Add(new BackupRepositoryDepartment { RepositoryId = repo.Id, DepartmentId = parent.Id });
        await db.SaveChangesAsync();
        return (parent, child, childShare, otherShare, repo);
    }

    private static BackupJobDraft Draft(Guid repositoryId, params Guid[] shareIds)
        => new(null, "Nightly", repositoryId, true, false, shareIds, [new TimeOnly(2, 0)], [DayOfWeek.Monday]);

    [Fact]
    public async Task SaveJob_ScopedAdmin_OnRepositoryReleasedToParentDepartment_Succeeds()
    {
        var (_, _, childShare, _, repo) = await SeedAsync(releaseToParent: true);
        ScopedJobAdmin(childShare.Id);

        var job = await Catalog().SaveJobAsync(Actor, Draft(repo.Id, childShare.Id));

        await using var db = NewContext();
        var saved = await db.BackupJobs.Include(j => j.Sources).SingleAsync(j => j.Id == job.Id);
        Assert.Equal(childShare.Id, Assert.Single(saved.Sources).ShareId);
        Assert.Equal(_user.Id, saved.CreatedByUserId);
        Assert.Equal([new TimeOnly(2, 0)], saved.Schedule.Times);
        // A new job never catches up on slots from before it existed.
        Assert.NotNull(saved.LastScheduledSlotUtc);
    }

    [Theory]
    [InlineData(false, true)]   // weekdays left, all start times removed
    [InlineData(true, false)]   // start times left, all weekdays removed
    [InlineData(false, false)]
    public async Task SaveJob_WithoutTimesOrDays_IsStoredAsManualOnly(bool withTimes, bool withDays)
    {
        var (_, _, childShare, _, repo) = await SeedAsync(releaseToParent: true);
        ScopedJobAdmin(childShare.Id);

        var job = await Catalog().SaveJobAsync(Actor, Draft(repo.Id, childShare.Id) with
        {
            Times = withTimes ? [new TimeOnly(2, 0)] : [],
            Days = withDays ? Enum.GetValues<DayOfWeek>() : [],
        });

        await using var db = NewContext();
        var schedule = (await db.BackupJobs.SingleAsync(j => j.Id == job.Id)).Schedule;
        Assert.Empty(schedule.Times);
        Assert.Empty(schedule.Days);
        Assert.True(schedule.IsEmpty);
    }

    [Fact]
    public async Task SaveJob_ScopedAdmin_OnUnreleasedRepository_IsRejected()
    {
        var (_, _, childShare, _, repo) = await SeedAsync(releaseToParent: false);
        ScopedJobAdmin(childShare.Id);

        var ex = await Assert.ThrowsAsync<ResticException>(() => Catalog().SaveJobAsync(Actor, Draft(repo.Id, childShare.Id)));
        Assert.Equal("repository_not_usable", ex.Code);
    }

    [Fact]
    public async Task SaveJob_ScopedAdmin_WithShareOutsideScope_IsRejected()
    {
        var (_, _, childShare, otherShare, repo) = await SeedAsync(releaseToParent: true);
        ScopedJobAdmin(childShare.Id);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            Catalog().SaveJobAsync(Actor, Draft(repo.Id, childShare.Id, otherShare.Id)));
        await using var db = NewContext();
        Assert.False(await db.BackupJobs.AnyAsync());
    }

    [Fact]
    public async Task SaveJob_ScopedAdmin_CannotBackUpTheHomesShare()
    {
        var (_, child, _, _, repo) = await SeedAsync(releaseToParent: true);
        var homes = new ShareDefinition("users", "/data/storage/pool01/users", child.Id) { IsUserHomes = true };
        await using (var db = NewContext())
        {
            db.ShareDefinitions.Add(homes);
            await db.SaveChangesAsync();
        }
        ScopedJobAdmin(homes.Id);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => Catalog().SaveJobAsync(Actor, Draft(repo.Id, homes.Id)));
        Assert.DoesNotContain(await Catalog().ListSelectableSharesAsync(Actor), s => s.Id == homes.Id);
    }

    [Fact]
    public async Task ScopedAdmin_DoesNotSeeOtherDepartmentsJobs()
    {
        var (_, _, childShare, otherShare, repo) = await SeedAsync(releaseToParent: true);
        await using (var db = NewContext())
        {
            db.BackupJobs.Add(new BackupJob
            {
                Name = "Sales nightly",
                RepositoryId = repo.Id,
                Sources = [new BackupJobSource { ShareId = otherShare.Id }],
            });
            await db.SaveChangesAsync();
        }
        ScopedJobAdmin(childShare.Id);

        Assert.Empty(await Catalog().ListJobsAsync(Actor));
        Assert.Empty(await Catalog().ListRunsAsync(Actor));
    }

    [Fact]
    public async Task RepositoryOperations_RequireGlobalPermission_AndDemoBlocksWrites()
    {
        await SeedAsync(releaseToParent: true);
        ScopedJobAdmin();
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => Catalog().ListRepositoriesAsync(Actor));

        _auth.Setup(a => a.HasGlobalPermissionAsync(It.IsAny<UserContext>(), ManagementPermission.ManageBackupRepositories)).ReturnsAsync(true);
        Assert.Single(await Catalog().ListRepositoriesAsync(Actor));
        var draft = new BackupRepositoryDraft("x", BackupBackend.Local, new ResticRepositorySettings(), new ResticBackendSecrets(), false, 5, []);
        await Assert.ThrowsAsync<ReadOnlyDemoException>(() => Catalog(demo: true).CreateRepositoryAsync(Actor, draft, null));
    }

    [Fact]
    public async Task DeletingAShare_CascadesToItsJobSources()
    {
        var (_, _, childShare, _, repo) = await SeedAsync(releaseToParent: true);
        await using (var db = NewContext())
        {
            db.BackupJobs.Add(new BackupJob { Name = "j", RepositoryId = repo.Id, Sources = [new BackupJobSource { ShareId = childShare.Id }] });
            await db.SaveChangesAsync();
        }
        await using (var db = NewContext())
        {
            db.ShareDefinitions.Remove(await db.ShareDefinitions.SingleAsync(s => s.Id == childShare.Id));
            await db.SaveChangesAsync();
        }
        await using var check = NewContext();
        Assert.False(await check.BackupJobSources.AnyAsync());
        Assert.True(await check.BackupJobs.AnyAsync());
    }

    [Fact]
    public async Task SaveJob_StoresRetentionAndAlert_AndRejectsNegativeValues()
    {
        var (_, _, childShare, _, repo) = await SeedAsync(releaseToParent: true);
        ScopedJobAdmin(childShare.Id);

        var job = await Catalog().SaveJobAsync(Actor, Draft(repo.Id, childShare.Id) with
        {
            Retention = new BackupRetention { KeepDaily = 7, KeepMonthly = 12 },
            AlertAfterDays = 5,
        });

        await using (var db = NewContext())
        {
            var saved = await db.BackupJobs.SingleAsync(j => j.Id == job.Id);
            Assert.Equal(7, saved.Retention.KeepDaily);
            Assert.Equal(12, saved.Retention.KeepMonthly);
            Assert.Equal(5, saved.AlertAfterDays);
        }
        var ex = await Assert.ThrowsAsync<ResticException>(() => Catalog().SaveJobAsync(Actor, Draft(repo.Id, childShare.Id) with
        {
            Id = job.Id,
            Retention = new BackupRetention { KeepDaily = -1 },
        }));
        Assert.Equal("retention_invalid", ex.Code);
    }

    [Fact]
    public async Task UpdateRepository_RejectsAMirrorCycle()
    {
        var settings = new ResticRepositorySettings { Path = "/data/kaimo-backups/restic/x" }.Serialize();
        var a = new BackupRepository { Name = "A", Backend = BackupBackend.Local, State = BackupRepositoryState.Active, SettingsJson = settings };
        var b = new BackupRepository { Name = "B", Backend = BackupBackend.Local, State = BackupRepositoryState.Active, SettingsJson = settings, MirrorTargetId = a.Id };
        await using (var db = NewContext())
        {
            db.BackupRepositories.AddRange(a, b);
            await db.SaveChangesAsync();
        }
        _auth.Setup(x => x.HasGlobalPermissionAsync(It.IsAny<UserContext>(), ManagementPermission.ManageBackupRepositories)).ReturnsAsync(true);
        BackupRepositoryDraft Draft(Guid? mirror) => new("A", BackupBackend.Local, ResticRepositorySettings.Parse(settings),
            new ResticBackendSecrets(), false, 5, [], MirrorTargetId: mirror);

        var ex = await Assert.ThrowsAsync<ResticException>(() => Catalog().UpdateRepositoryAsync(Actor, a.Id, Draft(b.Id)));
        Assert.Equal("mirror_invalid", ex.Code);

        // Without the cycle the same mirror is fine: B no longer mirrors back.
        await using (var db = NewContext())
        {
            (await db.BackupRepositories.SingleAsync(r => r.Id == b.Id)).MirrorTargetId = null;
            await db.SaveChangesAsync();
        }
        await Catalog().UpdateRepositoryAsync(Actor, a.Id, Draft(b.Id));
        await using var check = NewContext();
        Assert.Equal(b.Id, (await check.BackupRepositories.SingleAsync(r => r.Id == a.Id)).MirrorTargetId);
    }

    [Fact]
    public async Task MirrorCandidates_OnlyRepositoriesOneCopyCallCanServe()
    {
        // The "encrypted" secrets are plain markers the fake vault maps to access keys.
        var keys = new Dictionary<string, ResticBackendSecrets>
        {
            ["keys-a"] = new() { S3AccessKeyId = "a", S3SecretAccessKey = "x" },
            ["keys-b"] = new() { S3AccessKeyId = "b", S3SecretAccessKey = "y" },
        };
        var vault = new Mock<ICredentialVault>();
        vault.Setup(v => v.Unprotect<ResticBackendSecrets>(It.IsAny<string>(), It.IsAny<CredentialContext>()))
            .Returns((string value, CredentialContext _) => keys[value]);
        var s3 = new ResticRepositorySettings { Endpoint = "https://s3.example.com", Bucket = "bucket-1" };
        BackupRepository Repo(string name, BackupBackend backend, string? secrets = null, BackupRepositoryState state = BackupRepositoryState.Active) => new()
        {
            Name = name,
            Backend = backend,
            State = state,
            EncryptedSecrets = secrets,
            SettingsJson = (backend == BackupBackend.S3 ? s3 : new ResticRepositorySettings { Path = "/data/kaimo-backups/restic/" + name }).Serialize(),
        };
        var source = Repo("source", BackupBackend.S3, "keys-a");
        var sameKeys = Repo("s3-same-keys", BackupBackend.S3, "keys-a");
        var otherKeys = Repo("s3-other-keys", BackupBackend.S3, "keys-b");
        var local = Repo("local", BackupBackend.Local);
        var disabled = Repo("local-disabled", BackupBackend.Local, state: BackupRepositoryState.Disabled);
        var cycle = Repo("mirrors-into-source", BackupBackend.Local);
        cycle.MirrorTargetId = source.Id;
        await using (var db = NewContext())
        {
            db.BackupRepositories.AddRange(source, sameKeys, otherKeys, local, disabled, cycle);
            await db.SaveChangesAsync();
        }
        _auth.Setup(x => x.HasGlobalPermissionAsync(It.IsAny<UserContext>(), ManagementPermission.ManageBackupRepositories)).ReturnsAsync(true);

        // Editing the source: empty secret fields keep the stored keys.
        var draft = new BackupRepositoryDraft("source", BackupBackend.S3, s3, new ResticBackendSecrets(), false, 5, []);
        var names = (await Catalog(vault: vault.Object).ListMirrorCandidatesAsync(Actor, source.Id, draft)).Select(c => c.Name);
        Assert.Equal(["local", "s3-same-keys"], names);

        // A new repository with typed keys of the other account matches the other bucket instead.
        var typed = draft with { Name = "new", Secrets = new ResticBackendSecrets { S3AccessKeyId = "b", S3SecretAccessKey = "y" } };
        names = (await Catalog(vault: vault.Object).ListMirrorCandidatesAsync(Actor, null, typed)).Select(c => c.Name);
        Assert.Equal(["local", "mirrors-into-source", "s3-other-keys"], names);
    }

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

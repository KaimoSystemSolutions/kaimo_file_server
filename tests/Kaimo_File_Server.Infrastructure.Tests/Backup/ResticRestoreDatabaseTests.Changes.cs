using Kaimo_File_Server.Core.Domain.Backup;
using Kaimo_File_Server.Core.Domain.Identity;
using Kaimo_File_Server.Infrastructure.Backup;
using Kaimo_File_Server.Infrastructure.Backup.Restic;
using Microsoft.EntityFrameworkCore;
using Moq;
using Xunit;

namespace Kaimo_File_Server.Infrastructure.Tests.Backup;

/// <summary>
/// "What changed": a share backup point compared with the previous one of the same share in the
/// same repository (<c>restic diff</c>), under the same scope rules as browsing the point.
/// </summary>
public sealed partial class ResticRestoreDatabaseTests
{
    private const string ProjectsRoot = "/data/storage/pool01/projects";

    /// <summary>restic command lines run by the tests below.</summary>
    private readonly List<IReadOnlyList<string>> _calls = [];

    /// <summary>Answers <c>diff</c> with <paramref name="diff"/> and <c>ls</c> with <see cref="_lsOutput"/>.</summary>
    private void DiffOutput(params string[] diff)
        => _process.Setup(p => p.RunStreamingAsync(It.IsAny<string>(), It.IsAny<IReadOnlyList<string>>(),
                It.IsAny<IReadOnlyDictionary<string, string>?>(), It.IsAny<Action<string>?>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .Returns((string _, IReadOnlyList<string> args, IReadOnlyDictionary<string, string>? _, Action<string>? onLine, bool _, CancellationToken _) =>
            {
                _calls.Add(args);
                foreach (var line in args.Contains("diff") ? diff : _lsOutput)
                    onLine?.Invoke(line);
                return Task.FromResult(new ProcessResult(0, "", ""));
            });

    private static string Change(string modifier, string path)
        => $$"""{"message_type":"change","path":"{{path}}","modifier":"{{modifier}}"}""";

    /// <summary>Adds an older backup point of <paramref name="shareId"/>; <paramref name="repositoryId"/> defaults to the seeded one.</summary>
    private async Task AddPointAsync(Seed seed, Guid shareId, string snapshotId, DateTime timeUtc, Guid? repositoryId = null, string? root = null, Guid? jobId = null)
    {
        await using var db = NewContext();
        db.BackupSnapshots.Add(new BackupSnapshot
        {
            RepositoryId = repositoryId ?? seed.Repo.Id,
            SnapshotId = snapshotId,
            Kind = BackupSnapshotKind.Share,
            ShareId = shareId,
            JobId = jobId,
            TimeUtc = timeUtc,
            PathsJson = $"[\"{root ?? ProjectsRoot}\",\"/meta/share-x/kaimo-acl-manifest.json\"]",
        });
        await db.SaveChangesAsync();
    }

    private async Task<BackupRepository> AddRepositoryAsync()
    {
        var other = new BackupRepository { Name = "Offsite", Backend = BackupBackend.Local, State = BackupRepositoryState.Active, SettingsJson = "{}" };
        await using var db = NewContext();
        db.BackupRepositories.Add(other);
        await db.SaveChangesAsync();
        return other;
    }

    private List<string> DiffCall() => Assert.Single(_calls, c => c.Contains("diff")).ToList();

    [Fact]
    public async Task Changes_CompareWithThePreviousPointOfTheSameShareInTheSameRepository()
    {
        var seed = await SeedAsync();
        Restorer([seed.Projects.Id], []);
        var offsite = await AddRepositoryAsync();
        await AddPointAsync(seed, seed.Projects.Id, "older", new DateTime(2026, 10, 7, 0, 0, 0, DateTimeKind.Utc));
        await AddPointAsync(seed, seed.Projects.Id, "previous", new DateTime(2026, 10, 8, 0, 0, 0, DateTimeKind.Utc),
            root: "/data/storage/pool02/projects");
        // Closer in time, but another repository (restic cannot compare across repositories) or share.
        await AddPointAsync(seed, seed.Projects.Id, "offsite", new DateTime(2026, 10, 8, 12, 0, 0, DateTimeKind.Utc), offsite.Id);
        await AddPointAsync(seed, seed.Sales.Id, "sales-previous", new DateTime(2026, 10, 8, 18, 0, 0, DateTimeKind.Utc));
        DiffOutput();

        var changes = await Catalog().GetChangesAsync(Actor, seed.Repo.Id, "snap-projects");

        Assert.Equal("previous", changes.BaseSnapshotId);
        Assert.Equal(new DateTime(2026, 10, 8, 0, 0, 0, DateTimeKind.Utc), changes.BaseTimeUtc);
        var call = DiffCall();
        // Each side is compared at its own share folder (the share may have moved pools) and
        // without the ACL manifest next to it.
        Assert.Equal(["previous:/data/storage/pool02/projects", "snap-projects:/data/storage/pool01/projects"], call.Skip(call.IndexOf("--") + 1));
        Assert.Contains("--json", call);
        Assert.DoesNotContain("--metadata", call);
    }

    private async Task SetJobAsync(string snapshotId, Guid jobId)
    {
        await using var db = NewContext();
        var snapshot = await db.BackupSnapshots.SingleAsync(s => s.SnapshotId == snapshotId);
        snapshot.JobId = jobId;
        await db.SaveChangesAsync();
    }

    [Fact]
    public async Task Changes_ByDefault_CompareWithThePreviousPointOfTheSameJob()
    {
        var seed = await SeedAsync();
        Restorer([seed.Projects.Id], []);
        var daily = Guid.NewGuid();
        await SetJobAsync("snap-projects", daily);
        await AddPointAsync(seed, seed.Projects.Id, "daily-previous", new DateTime(2026, 10, 8, 0, 0, 0, DateTimeKind.Utc), jobId: daily);
        // Closer in time, but another job: it may include the recycle bin or versions, which
        // would show up as changes that are none.
        await AddPointAsync(seed, seed.Projects.Id, "weekly", new DateTime(2026, 10, 8, 12, 0, 0, DateTimeKind.Utc), jobId: Guid.NewGuid());
        DiffOutput();

        var changes = await Catalog().GetChangesAsync(Actor, seed.Repo.Id, "snap-projects");

        Assert.Equal("daily-previous", changes.BaseSnapshotId);
    }

    [Fact]
    public async Task Changes_WithoutAnEarlierPointOfTheSameJob_CompareWithTheShareBefore()
    {
        var seed = await SeedAsync();
        Restorer([seed.Projects.Id], []);
        await SetJobAsync("snap-projects", Guid.NewGuid());
        await AddPointAsync(seed, seed.Projects.Id, "weekly", new DateTime(2026, 10, 8, 12, 0, 0, DateTimeKind.Utc), jobId: Guid.NewGuid());
        DiffOutput();

        var changes = await Catalog().GetChangesAsync(Actor, seed.Repo.Id, "snap-projects");

        Assert.Equal("weekly", changes.BaseSnapshotId);
    }

    [Fact]
    public async Task Changes_AgainstANewerPoint_AreRefused()
    {
        var seed = await SeedAsync();
        Restorer([seed.Projects.Id], []);
        await AddPointAsync(seed, seed.Projects.Id, "newer", new DateTime(2026, 10, 10, 0, 0, 0, DateTimeKind.Utc));
        DiffOutput();

        // Compared the other way round, "added" and "removed" would swap meaning.
        var error = await Assert.ThrowsAsync<ResticException>(() =>
            Catalog().GetChangesAsync(Actor, seed.Repo.Id, "snap-projects", "newer"));
        Assert.Equal("snapshot_missing", error.Code);
        Assert.Empty(_calls);
    }

    [Fact]
    public async Task Changes_RunOnlyALimitedNumberOfDiffsAtOnce()
    {
        var seed = await SeedAsync();
        Restorer([seed.Projects.Id], []);
        await AddPointAsync(seed, seed.Projects.Id, "previous", new DateTime(2026, 10, 8, 0, 0, 0, DateTimeKind.Utc));
        var release = new TaskCompletionSource<ProcessResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var started = 0;
        _process.Setup(p => p.RunStreamingAsync(It.IsAny<string>(), It.IsAny<IReadOnlyList<string>>(),
                It.IsAny<IReadOnlyDictionary<string, string>?>(), It.IsAny<Action<string>?>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .Returns(() =>
            {
                Interlocked.Increment(ref started);
                return release.Task;
            });

        // Each diff loads two whole snapshot trees; many users must not start one each at once.
        var runs = Enumerable.Range(0, BackupCatalogService.MaximumParallelDiffs + 2)
            .Select(_ => Catalog().GetChangesAsync(OtherActor(), seed.Repo.Id, "snap-projects"))
            .ToList();
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (Volatile.Read(ref started) < BackupCatalogService.MaximumParallelDiffs && DateTime.UtcNow < deadline)
            await Task.Delay(20);
        await Task.Delay(200);
        Assert.Equal(BackupCatalogService.MaximumParallelDiffs, Volatile.Read(ref started));

        release.SetResult(new ProcessResult(0, "", ""));
        await Task.WhenAll(runs);
        Assert.Equal(runs.Count, started);
    }

    private static UserContext OtherActor() => new(new User(Guid.NewGuid(), "Other", "other", "hash", "nt"), [], [], []);

    /// <summary>Answers <c>diff</c> with a task that ends only when <paramref name="release"/> completes.</summary>
    private void DiffWaitsFor(TaskCompletionSource<ProcessResult> release, Action? onStart = null)
        => _process.Setup(p => p.RunStreamingAsync(It.IsAny<string>(), It.IsAny<IReadOnlyList<string>>(),
                It.IsAny<IReadOnlyDictionary<string, string>?>(), It.IsAny<Action<string>?>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .Returns((string _, IReadOnlyList<string> args, IReadOnlyDictionary<string, string>? _, Action<string>? _, bool _, CancellationToken _) =>
            {
                lock (_calls)
                    _calls.Add(args);
                onStart?.Invoke();
                return release.Task;
            });

    [Fact]
    public async Task Changes_ASecondDiffOfTheSameUser_IsRefusedWhileTheFirstRuns()
    {
        var seed = await SeedAsync();
        Restorer([seed.Projects.Id], []);
        await AddPointAsync(seed, seed.Projects.Id, "previous", new DateTime(2026, 10, 8, 0, 0, 0, DateTimeKind.Utc));
        var release = new TaskCompletionSource<ProcessResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        DiffWaitsFor(release, () => started.TrySetResult());

        // One user (e.g. two browser tabs) must not take every diff slot and starve the others.
        var first = Catalog().GetChangesAsync(Actor, seed.Repo.Id, "snap-projects");
        await started.Task.WaitAsync(TimeSpan.FromSeconds(10));
        // Refused at once, not queued behind the first one (which would wait forever here).
        var error = await Assert.ThrowsAsync<ResticException>(() =>
            Catalog().GetChangesAsync(Actor, seed.Repo.Id, "snap-projects").WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.Equal("changes_busy", error.Code);
        Assert.Single(_calls, c => c.Contains("diff"));

        release.SetResult(new ProcessResult(0, "", ""));
        await first;
        // Finished: the user may compare again.
        await Catalog().GetChangesAsync(Actor, seed.Repo.Id, "snap-projects");
    }

    [Fact]
    public async Task Changes_ThatTimeOut_ReportAComparisonTimeout_AndFreeTheUsersSlot()
    {
        var seed = await SeedAsync();
        Restorer([seed.Projects.Id], []);
        await AddPointAsync(seed, seed.Projects.Id, "previous", new DateTime(2026, 10, 8, 0, 0, 0, DateTimeKind.Utc));
        // The interactive timeout cancels the process run without the caller having cancelled.
        var timedOut = new TaskCompletionSource<ProcessResult>();
        timedOut.SetException(new OperationCanceledException());
        DiffWaitsFor(timedOut);

        // Not "timeout" ("check address and credentials"): a big share or a full queue is no
        // connection problem.
        var error = await Assert.ThrowsAsync<ResticException>(() => Catalog().GetChangesAsync(Actor, seed.Repo.Id, "snap-projects"));
        Assert.Equal("changes_timeout", error.Code);

        DiffOutput();
        await Catalog().GetChangesAsync(Actor, seed.Repo.Id, "snap-projects");
    }

    [Fact]
    public async Task Changes_AreClassifiedShareRelative_WithoutFolderNoiseOrKaimoInternals()
    {
        var seed = await SeedAsync();
        Restorer([seed.Projects.Id], []);
        await AddPointAsync(seed, seed.Projects.Id, "previous", new DateTime(2026, 10, 8, 0, 0, 0, DateTimeKind.Utc));
        DiffOutput(
            "not json",
            Change("+", "/new.txt"),
            Change("-", "/gone/"),
            Change("-", "/gone/a.txt"),
            Change("M", "/docs/report.docx"),
            Change("T", "/docs/link"),
            Change("U", "/docs/"),
            Change("U", "/docs/touched.txt"),
            Change("+", "/.kaimo-meta/x.json"),
            Change("+", "/docs/.kaimo-tmp/y"),
            """{"message_type":"statistics","changed_files":2,"added":{"files":1},"removed":{"files":1}}""");

        var changes = await Catalog().GetChangesAsync(Actor, seed.Repo.Id, "snap-projects");

        Assert.Equal(
        [
            new BackupChange("new.txt", BackupChangeKind.Added, false),
            new BackupChange("gone", BackupChangeKind.Removed, true),
            new BackupChange("gone/a.txt", BackupChangeKind.Removed, false),
            new BackupChange("docs/report.docx", BackupChangeKind.Modified, false),
            new BackupChange("docs/link", BackupChangeKind.Modified, false),
        ], changes.Items);
        // Metadata-only lines ("U", printed by restic only with --metadata) are no news.
        Assert.Equal((1, 2, 2), (changes.Added, changes.Modified, changes.Removed));
        Assert.False(changes.Truncated);
        Assert.False(changes.IsFirstPoint);
    }

    [Fact]
    public async Task Changes_OfTheFirstPoint_RunNoDiff()
    {
        var seed = await SeedAsync();
        Restorer([seed.Projects.Id], []);
        DiffOutput(Change("+", "/a.txt"));

        var changes = await Catalog().GetChangesAsync(Actor, seed.Repo.Id, "snap-projects");

        Assert.True(changes.IsFirstPoint);
        Assert.Null(changes.BaseSnapshotId);
        Assert.Empty(changes.Items);
        Assert.Empty(_calls);
    }

    [Fact]
    public async Task Changes_AgainstAChosenPoint_MustBeOfTheSameShareAndRepository()
    {
        var seed = await SeedAsync();
        Restorer([seed.Projects.Id, seed.Sales.Id], []);
        var offsite = await AddRepositoryAsync();
        await AddPointAsync(seed, seed.Projects.Id, "older", new DateTime(2026, 10, 7, 0, 0, 0, DateTimeKind.Utc));
        await AddPointAsync(seed, seed.Projects.Id, "previous", new DateTime(2026, 10, 8, 0, 0, 0, DateTimeKind.Utc));
        await AddPointAsync(seed, seed.Projects.Id, "offsite", new DateTime(2026, 10, 6, 0, 0, 0, DateTimeKind.Utc), offsite.Id);
        DiffOutput();

        var changes = await Catalog().GetChangesAsync(Actor, seed.Repo.Id, "snap-projects", "older");
        Assert.Equal("older", changes.BaseSnapshotId);
        Assert.StartsWith("older:", DiffCall()[^2]);

        foreach (var other in new[] { "snap-sales", "offsite", "snap-projects", "missing" })
        {
            var error = await Assert.ThrowsAsync<ResticException>(() =>
                Catalog().GetChangesAsync(Actor, seed.Repo.Id, "snap-projects", other));
            Assert.Equal("snapshot_missing", error.Code);
        }
    }

    [Fact]
    public async Task Changes_OutsideTheRestoreScope_AreRefused()
    {
        var seed = await SeedAsync();
        Restorer([seed.Sales.Id], []);
        await AddPointAsync(seed, seed.Projects.Id, "previous", new DateTime(2026, 10, 8, 0, 0, 0, DateTimeKind.Utc));
        DiffOutput(Change("+", "/secret.txt"));

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            Catalog().GetChangesAsync(Actor, seed.Repo.Id, "snap-projects"));
        Assert.Empty(_calls);
    }

    [Fact]
    public async Task Changes_BeyondTheListLimit_AreStillCounted()
    {
        var seed = await SeedAsync();
        Restorer([seed.Projects.Id], []);
        await AddPointAsync(seed, seed.Projects.Id, "previous", new DateTime(2026, 10, 8, 0, 0, 0, DateTimeKind.Utc));
        var total = BackupCatalogService.MaximumListedChanges + 3;
        DiffOutput(Enumerable.Range(0, total).Select(i => Change("+", $"/f{i}.txt")).ToArray());

        var changes = await Catalog().GetChangesAsync(Actor, seed.Repo.Id, "snap-projects");

        Assert.Equal(BackupCatalogService.MaximumListedChanges, changes.Items.Count);
        Assert.Equal(total, changes.Added);
        Assert.True(changes.Truncated);
    }

    [Fact]
    public async Task Changes_OnTheHomesShare_ShowOnlyReachableHomes()
    {
        var seed = await SeedAsync();
        GlobalRestorer(accessHomes: true, _reachable);
        await AddPointAsync(seed, seed.Homes.Id, "homes-previous", new DateTime(2026, 10, 8, 0, 0, 0, DateTimeKind.Utc),
            root: "/data/storage/pool01/users");
        HomesRootListing();
        DiffOutput(
            Change("+", $"/{_reachable}/new.txt"),
            Change("M", $"/{_boss}/salary.xlsx"),
            Change("-", $"/{_boss}/"),
            Change("+", "/readme.txt"));

        var changes = await Catalog().GetChangesAsync(Actor, seed.Repo.Id, "snap-users");

        Assert.Equal([new BackupChange($"{_reachable}/new.txt", BackupChangeKind.Added, false)], changes.Items);
        Assert.Equal((1, 0, 0), (changes.Added, changes.Modified, changes.Removed));
    }

    [Fact]
    public async Task Changes_OnTheHomesShare_WithoutAccessHomeContents_AreRefused()
    {
        var seed = await SeedAsync();
        GlobalRestorer(accessHomes: false);
        await AddPointAsync(seed, seed.Homes.Id, "homes-previous", new DateTime(2026, 10, 8, 0, 0, 0, DateTimeKind.Utc),
            root: "/data/storage/pool01/users");
        DiffOutput(Change("+", $"/{_reachable}/new.txt"));

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => Catalog().GetChangesAsync(Actor, seed.Repo.Id, "snap-users"));
    }
}

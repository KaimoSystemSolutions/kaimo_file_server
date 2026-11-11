using Kaimo_File_Server.Core.Domain;
using Kaimo_File_Server.Core.Domain.Backup;
using Kaimo_File_Server.Core.Domain.Identity;
using Kaimo_File_Server.Core.Repositories;
using Kaimo_File_Server.Core.Security;
using Kaimo_File_Server.Core.Services;
using Kaimo_File_Server.Core.Services.File;
using Kaimo_File_Server.Infrastructure.Backup;
using Kaimo_File_Server.Infrastructure.Backup.Restic;
using Kaimo_File_Server.Web.Components.ViewModels.Backup;
using Kaimo_File_Server.Web.Services;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Kaimo_File_Server.Web.Tests.ViewModels.Backup;

/// <summary>
/// The "Changes" view of the restore tab against the real catalog (Sqlite) and a scripted
/// restic: it never shows the comparison of another backup point, can be cancelled, offers a
/// retry after a failure and never opens a removed entry in the wrong backup point.
/// </summary>
public sealed class BackupViewModelChangesTests : DatabaseTestBase, IDisposable
{
    private const string Root = "/data/storage/pool01/projects";
    private static readonly DateTime NewTime = new(2026, 10, 9, 0, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime OldTime = new(2026, 10, 8, 0, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime OldestTime = new(2026, 10, 7, 0, 0, 0, DateTimeKind.Utc);

    private readonly string _root = Path.Combine(Path.GetTempPath(), "kaimo-changes-vm-" + Guid.NewGuid().ToString("N"));
    private readonly Mock<IManagementAuthService> _auth = new();
    private readonly Mock<IProcessRunner> _process = new();
    private readonly List<IReadOnlyList<string>> _calls = [];
    private readonly User _user;
    private readonly ShareDefinition _share;
    private readonly BackupRepository _repo;

    /// <summary>How <c>restic diff</c> answers; default: one added file.</summary>
    private Func<IReadOnlyList<string>, Action<string>?, CancellationToken, Task<ProcessResult>> _diff;

    /// <summary>When true, <c>restic ls</c> fails (repository offline).</summary>
    private bool _lsFails;

    public BackupViewModelChangesTests()
    {
        _user = SeedUser("alice");
        _share = SeedShare("projects", Root);
        _repo = new BackupRepository
        {
            Name = "NAS",
            Backend = BackupBackend.Local,
            State = BackupRepositoryState.Active,
            SettingsJson = new ResticRepositorySettings { Path = Path.Combine(_root, "repo") }.Serialize(),
            EncryptedPassword = "protected",
        };
        using (var db = NewContext())
        {
            db.BackupRepositories.Add(_repo);
            db.SaveChanges();
        }
        AddPoint("point-new", NewTime);
        AddPoint("point-old", OldTime);
        AddPoint("point-oldest", OldestTime);

        var scope = AuthorizedScopeResult.LimitedTo([_share.Id]);
        _auth.Setup(a => a.HasGlobalPermissionAsync(It.IsAny<UserContext>(), It.IsAny<ManagementPermission>())).ReturnsAsync(false);
        _auth.Setup(a => a.HasAnyPermissionAsync(It.IsAny<UserContext>(), It.IsAny<ManagementPermission>())).ReturnsAsync(false);
        _auth.Setup(a => a.GetAuthorizedShareIdsAsync(It.IsAny<UserContext>(), It.IsAny<ManagementPermission>())).ReturnsAsync(scope);
        _auth.Setup(a => a.GetAuthorizedShareIdsAnyAsync(It.IsAny<UserContext>(), It.IsAny<ManagementPermission>())).ReturnsAsync(scope);

        _diff = (_, onLine, _) =>
        {
            onLine?.Invoke("""{"message_type":"change","path":"/new.txt","modifier":"+"}""");
            return Task.FromResult(new ProcessResult(0, "", ""));
        };
        _process.Setup(p => p.RunStreamingAsync(It.IsAny<string>(), It.IsAny<IReadOnlyList<string>>(),
                It.IsAny<IReadOnlyDictionary<string, string>?>(), It.IsAny<Action<string>?>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .Returns((string _, IReadOnlyList<string> args, IReadOnlyDictionary<string, string>? _, Action<string>? onLine, bool _, CancellationToken ct) =>
            {
                lock (_calls)
                    _calls.Add(args);
                if (args.Contains("diff"))
                    return _diff(args, onLine, ct);
                if (_lsFails)
                    return Task.FromResult(new ProcessResult(1, "", "Fatal: unable to open repository"));
                onLine?.Invoke($$"""{"name":"docs","type":"dir","path":"{{Root}}/docs","message_type":"node"}""");
                onLine?.Invoke($$"""{"name":"gone.txt","type":"file","path":"{{Root}}/gone.txt","size":3,"message_type":"node"}""");
                return Task.FromResult(new ProcessResult(0, "", ""));
            });
    }

    public new void Dispose()
    {
        base.Dispose();
        try { Directory.Delete(_root, recursive: true); } catch (DirectoryNotFoundException) { }
    }

    private void AddPoint(string snapshotId, DateTime timeUtc)
    {
        using var db = NewContext();
        db.BackupSnapshots.Add(new BackupSnapshot
        {
            RepositoryId = _repo.Id,
            SnapshotId = snapshotId,
            Kind = BackupSnapshotKind.Share,
            ShareId = _share.Id,
            TimeUtc = timeUtc,
            PathsJson = $"[\"{Root}\",\"/meta/share-x/kaimo-acl-manifest.json\"]",
        });
        db.SaveChanges();
    }

    private async Task<BackupViewModel> LoadedAsync()
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Backup:Restic:LocalRoots"] = _root,
            ["Backup:Restic:CacheDirectory"] = Path.Combine(_root, "cache"),
        }).Build();
        var vault = new Mock<ICredentialVault>();
        vault.Setup(v => v.Unprotect<string>(It.IsAny<string>(), It.IsAny<CredentialContext>())).Returns("pw");
        var resolver = new ResticTargetResolver(DbFactory, vault.Object, config);
        var restic = new ResticClient(_process.Object, config);
        var catalog = new BackupCatalogService(DbFactory, _auth.Object, resolver, restic, Mock.Of<IBackupRunner>(),
            new DemoModeOptions(), TimeProvider.System, NullLogger<BackupCatalogService>.Instance);
        var executor = new BackupRestoreExecutor(DbFactory, resolver, restic, Mock.Of<IFileVersionService>(),
            Mock.Of<IFileChangeLog>(), new ShareLockManager(), config, TimeProvider.System, NullLogger<BackupRestoreExecutor>.Instance);
        var vm = new BackupViewModel(AuthStateFor("alice"), UserContextFactoryFor(("alice", ContextFor(_user))).Object, catalog,
            Mock.Of<IBackupRunner>(), Mock.Of<IDepartmentRepository>(), new BackupDownloadTokenService(new EphemeralDataProtectionProvider()),
            executor, new DemoModeOptions(), TimeProvider.System, NullLogger<BackupViewModel>.Instance);
        await vm.LoadAsync();
        Assert.True(await vm.SelectRestoreShareAsync(_share.Id), vm.ErrorMessage);
        return vm;
    }

    private string Key(string snapshotId) => _repo.Id.ToString("N") + ":" + snapshotId;

    private int Calls(string command)
    {
        lock (_calls)
            return _calls.Count(c => c.Contains(command));
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!condition() && DateTime.UtcNow < deadline)
            await Task.Delay(10);
        Assert.True(condition());
    }

    /// <summary>A diff that runs until it is cancelled.</summary>
    private void DiffHangs()
        => _diff = async (_, _, ct) =>
        {
            await Task.Delay(Timeout.Infinite, ct);
            return new ProcessResult(0, "", "");
        };

    // ── 1: never the comparison of another backup point ─────────────────

    [Fact]
    public async Task SwitchingThePoint_InTheChangesView_ComparesTheNewPointWithoutListingContent()
    {
        var vm = await LoadedAsync();
        Assert.True(await vm.ShowChangesAsync(true));
        Assert.Equal("point-old", vm.Changes?.BaseSnapshotId);
        _lsFails = true;
        var listings = Calls("ls");

        Assert.True(await vm.SelectBackupPointAsync(Key("point-old")), vm.ErrorMessage);

        Assert.Equal("point-oldest", vm.Changes?.BaseSnapshotId);
        Assert.Equal(listings, Calls("ls"));
    }

    [Fact]
    public async Task AFailedComparison_AfterSwitchingTheShareOrPoint_LeavesNoStaleResult()
    {
        var vm = await LoadedAsync();
        Assert.True(await vm.ShowChangesAsync(true));
        Assert.NotNull(vm.Changes);
        _diff = (_, _, _) => Task.FromResult(new ProcessResult(1, "", "Fatal: unable to open repository"));

        Assert.False(await vm.SelectBackupPointAsync(Key("point-old")));
        Assert.Null(vm.Changes);
        Assert.True(vm.ShowChanges);

        // Back on the newest point: its own (cached) comparison, not the failed one's leftovers.
        Assert.True(await vm.SelectRestoreShareAsync(_share.Id));
        Assert.Equal("point-new", vm.BackupPoint?.SnapshotId);
        Assert.Equal("point-old", vm.Changes?.BaseSnapshotId);
    }

    [Fact]
    public async Task BackToTheContentView_ListsTheSelectedPoint()
    {
        var vm = await LoadedAsync();
        Assert.True(await vm.ShowChangesAsync(true));
        Assert.True(await vm.SelectBackupPointAsync(Key("point-old")));
        var listings = Calls("ls");

        Assert.True(await vm.ShowChangesAsync(false));

        Assert.Equal(listings + 1, Calls("ls"));
        Assert.Contains(vm.BrowseEntries, e => e.Name == "docs");
        Assert.Contains("point-old", _calls.Last(c => c.Contains("ls")));
    }

    // ── 2: cancellable, and nothing keeps running for a closed page ─────

    [Fact]
    public async Task CancelChanges_StopsTheDiffAndFreesThePage_WithoutAnError()
    {
        var vm = await LoadedAsync();
        DiffHangs();

        var loading = vm.ShowChangesAsync(true);
        await WaitUntilAsync(() => vm.IsLoadingChanges && Calls("diff") == 1);
        Assert.True(vm.IsBusy);
        vm.CancelChanges();
        await loading;

        Assert.False(vm.IsBusy);
        Assert.False(vm.IsLoadingChanges);
        Assert.Null(vm.Changes);
        Assert.Null(vm.ErrorMessage);
    }

    [Fact]
    public async Task Dispose_CancelsARunningDiff()
    {
        var vm = await LoadedAsync();
        CancellationToken diffToken = default;
        _diff = async (_, _, ct) =>
        {
            diffToken = ct;
            await Task.Delay(Timeout.Infinite, ct);
            return new ProcessResult(0, "", "");
        };

        var loading = vm.ShowChangesAsync(true);
        await WaitUntilAsync(() => Calls("diff") == 1);
        vm.Dispose();
        await loading;

        Assert.True(diffToken.IsCancellationRequested);
    }

    [Fact]
    public async Task OpenChangesFromTheActivityLog_SelectsThePointWithoutWaitingForTheDiff()
    {
        var vm = await LoadedAsync();
        _calls.Clear();

        Assert.True(await vm.OpenChangesAsync(_share.Id, _repo.Id, "point-old"));

        // The page switches to the restore tab first, where the diff shows its progress.
        Assert.Equal("point-old", vm.BackupPoint?.SnapshotId);
        Assert.True(vm.ShowChanges);
        Assert.Empty(_calls);

        Assert.True(await vm.ShowChangesAsync(true));
        Assert.Equal("point-oldest", vm.Changes?.BaseSnapshotId);
    }

    // ── 3: a failed comparison can be retried with the same base ─────────

    [Fact]
    public async Task RetryChanges_RepeatsTheComparisonWithTheChosenBase()
    {
        var vm = await LoadedAsync();
        Assert.True(await vm.ShowChangesAsync(true));
        var working = _diff;
        _diff = (_, _, _) => Task.FromResult(new ProcessResult(1, "", "Fatal: unable to open repository"));
        Assert.False(await vm.SelectChangeBaseAsync("point-oldest"));
        Assert.Null(vm.Changes);
        Assert.NotNull(vm.ErrorMessage);

        _diff = working;
        Assert.True(await vm.RetryChangesAsync());

        Assert.Equal("point-oldest", vm.Changes?.BaseSnapshotId);
    }

    // ── 4: bounded cache, no second diff for the same comparison ────────

    [Fact]
    public async Task ChoosingTheDefaultBaseExplicitly_ReusesTheComparison()
    {
        var vm = await LoadedAsync();
        Assert.True(await vm.ShowChangesAsync(true));
        Assert.Equal("point-old", vm.Changes?.BaseSnapshotId);

        Assert.True(await vm.SelectChangeBaseAsync("point-old"));

        Assert.Equal(1, Calls("diff"));
        Assert.Equal("point-old", vm.Changes?.BaseSnapshotId);
    }

    [Fact]
    public async Task TheComparisonCache_IsBounded()
    {
        // Enough older points to compare the newest one with more bases than the cache holds.
        var bases = Enumerable.Range(0, BackupViewModel.MaximumCachedComparisons + 1)
            .Select(i => "point-base-" + i)
            .ToList();
        for (var i = 0; i < bases.Count; i++)
            AddPoint(bases[i], OldestTime.AddHours(-1 - i));
        var vm = await LoadedAsync();

        foreach (var point in bases)
            Assert.True(await vm.SelectChangeBaseAsync(point), vm.ErrorMessage);
        Assert.Equal(bases.Count, Calls("diff"));

        // The first comparison was dropped to make room, so it runs again.
        Assert.True(await vm.SelectChangeBaseAsync(bases[0]));
        Assert.Equal(bases.Count + 1, Calls("diff"));
    }

    [Fact]
    public async Task ASecondTabOfTheSameUser_GetsABusyMessage_WhileTheFirstCompares()
    {
        var first = await LoadedAsync();
        var second = await LoadedAsync();
        DiffHangs();

        var loading = first.ShowChangesAsync(true);
        await WaitUntilAsync(() => Calls("diff") == 1);

        Assert.False(await second.ShowChangesAsync(true));
        Assert.Equal(BackupViewModel.ErrorText("changes_busy"), second.ErrorMessage);
        Assert.Equal(1, Calls("diff"));

        first.CancelChanges();
        await loading;
    }

    [Theory]
    [InlineData("changes_busy")]
    [InlineData("changes_timeout")]
    public void ComparisonErrors_HaveTheirOwnText(string code)
        => Assert.DoesNotContain(code, BackupViewModel.ErrorText(code));

    [Fact]
    public void NoMatch_InATruncatedList_SaysOnlyTheListedChangesWereSearched()
    {
        var listed = new List<BackupChange> { new("a.txt", BackupChangeKind.Added, false) };
        var truncated = new BackupChangeSet("point-old", OldTime, listed, 7, 0, 0);
        var complete = new BackupChangeSet("point-old", OldTime, listed, 1, 0, 0);
        var empty = new BackupChangeSet("point-old", OldTime, [], 0, 0, 0);

        Assert.Equal(string.Format(BackupViewModel.R("Web_Backup_Changes_NoMatchTruncated"), 1),
            Kaimo_File_Server.Web.Components.Pages.Backup.Components.BackupChanges.NoRowsText(truncated));
        Assert.Equal(BackupViewModel.R("Web_Backup_Changes_NoMatch"),
            Kaimo_File_Server.Web.Components.Pages.Backup.Components.BackupChanges.NoRowsText(complete));
        Assert.Equal(BackupViewModel.R("Web_Backup_Changes_None"),
            Kaimo_File_Server.Web.Components.Pages.Backup.Components.BackupChanges.NoRowsText(empty));
    }

    // ── 5: a removed entry never opens in the wrong backup point ────────

    [Fact]
    public async Task ShowRemovedEntry_WhoseBasePointIsGone_ReportsItInsteadOfOpeningAnotherPoint()
    {
        var vm = await LoadedAsync();
        Assert.True(await vm.ShowChangesAsync(true));
        await using (var db = NewContext())
        {
            await db.BackupSnapshots.Where(s => s.SnapshotId == "point-old").ExecuteDeleteAsync();
        }
        // Reloading the share keeps the comparison cached, but the base point is gone.
        Assert.True(await vm.SelectRestoreShareAsync(_share.Id), vm.ErrorMessage);
        Assert.Equal("point-old", vm.Changes?.BaseSnapshotId);

        Assert.False(await vm.ShowChangeInContentAsync(new BackupChange("gone.txt", BackupChangeKind.Removed, false)));

        Assert.Equal(BackupViewModel.ErrorText("snapshot_missing"), vm.ErrorMessage);
        Assert.Equal("point-new", vm.BackupPoint?.SnapshotId);
        Assert.True(vm.ShowChanges);
    }

    [Fact]
    public async Task ShowRemovedEntry_OpensItSelectedInTheBasePoint()
    {
        var vm = await LoadedAsync();
        Assert.True(await vm.ShowChangesAsync(true));

        Assert.True(await vm.ShowChangeInContentAsync(new BackupChange("gone.txt", BackupChangeKind.Removed, false)), vm.ErrorMessage);

        Assert.Equal("point-old", vm.BackupPoint?.SnapshotId);
        Assert.False(vm.ShowChanges);
        Assert.Equal(["gone.txt"], vm.SelectedEntries);
    }
}

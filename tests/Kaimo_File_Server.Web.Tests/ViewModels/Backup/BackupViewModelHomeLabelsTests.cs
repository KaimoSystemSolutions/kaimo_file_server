using Kaimo_File_Server.Core.Domain;
using Kaimo_File_Server.Core.Domain.Backup;
using Kaimo_File_Server.Core.Domain.Identity;
using Kaimo_File_Server.Core.Repositories.Identity;
using Kaimo_File_Server.Core.Security.Credentials;
using Kaimo_File_Server.Core.Security.Demo;
using Kaimo_File_Server.Core.Security.Management;
using Kaimo_File_Server.Core.Services.File;
using Kaimo_File_Server.Core.Services.Management;
using Kaimo_File_Server.Core.Services.Versioning;
using Kaimo_File_Server.Infrastructure.Backup;
using Kaimo_File_Server.Infrastructure.Backup.Restic;
using Kaimo_File_Server.Web.Components.ViewModels.Backup;
using Kaimo_File_Server.Web.Services.Downloads;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Kaimo_File_Server.Web.Tests.ViewModels.Backup;

/// <summary>
/// The restore tab shows the home folders of the home-folder share (named by their owner's
/// GUID) as the owner's username, like the file browser: in the listing at the share root, in
/// the breadcrumb and folder labels, and in the paths of the changes view. Navigation and the
/// restore request keep the GUID; other shares are never relabelled. The activity log names the
/// source and target of a restore run the same way.
/// </summary>
public sealed class BackupViewModelHomeLabelsTests : DatabaseTestBase, IDisposable
{
    private const string HomesRoot = "/data/storage/pool01/users";
    private const string ProjectsRoot = "/data/storage/pool01/projects";

    private readonly string _root = Path.Combine(Path.GetTempPath(), "kaimo-home-labels-vm-" + Guid.NewGuid().ToString("N"));
    private readonly Mock<IManagementAuthService> _auth = new();
    private readonly Mock<IProcessRunner> _process = new();
    private readonly User _admin;
    private readonly User _bob;
    // Not seeded: only the user list knows them. Their GUIDs sort opposite to their usernames.
    private readonly User _zed = new(Guid.Parse("00000000-0000-0000-0000-0000000000aa"), "Zed", "zed", "hash", "nt");
    private readonly User _adam = new(Guid.Parse("ffffffff-0000-0000-0000-0000000000aa"), "Adam", "adam", "hash", "nt");
    private readonly string _orphan = "99999999-0000-0000-0000-0000000000aa";
    private readonly Mock<IUserRepository> _users = new();
    private readonly ShareDefinition _homes;
    private readonly ShareDefinition _projects;
    private readonly BackupRepository _repo;

    public BackupViewModelHomeLabelsTests()
    {
        _admin = SeedUser("admin");
        _bob = SeedUser("bob");
        _homes = new ShareDefinition("users", HomesRoot, null, true, false, false) { IsUserHomes = true };
        _projects = new ShareDefinition("projects", ProjectsRoot, null, true, false, false);
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
            db.ShareDefinitions.AddRange(_homes, _projects);
            db.BackupRepositories.Add(_repo);
            db.SaveChanges();
        }
        AddPoint(_homes, HomesRoot, "homes-new", new DateTime(2026, 10, 9, 0, 0, 0, DateTimeKind.Utc));
        AddPoint(_homes, HomesRoot, "homes-old", new DateTime(2026, 10, 8, 0, 0, 0, DateTimeKind.Utc));
        AddPoint(_projects, ProjectsRoot, "projects-new", new DateTime(2026, 10, 9, 0, 0, 0, DateTimeKind.Utc));

        var all = AuthorizedScopeResult.Unrestricted();
        _auth.Setup(a => a.HasGlobalPermissionAsync(It.IsAny<UserContext>(), It.IsAny<ManagementPermission>())).ReturnsAsync(false);
        _auth.Setup(a => a.HasAnyPermissionAsync(It.IsAny<UserContext>(), ManagementPermission.AccessHomeContents)).ReturnsAsync(true);
        _auth.Setup(a => a.CanManageUserAsync(It.IsAny<UserContext>(), It.IsAny<Guid>(), ManagementPermission.AccessHomeContents)).ReturnsAsync(true);
        _auth.Setup(a => a.GetAuthorizedShareIdsAsync(It.IsAny<UserContext>(), It.IsAny<ManagementPermission>())).ReturnsAsync(all);
        _auth.Setup(a => a.GetAuthorizedShareIdsAnyAsync(It.IsAny<UserContext>(), It.IsAny<ManagementPermission>())).ReturnsAsync(all);

        _users.Setup(u => u.GetAllAsync()).ReturnsAsync([_admin, _bob, _zed, _adam]);

        _process.Setup(p => p.RunStreamingAsync(It.IsAny<string>(), It.IsAny<IReadOnlyList<string>>(),
                It.IsAny<IReadOnlyDictionary<string, string>?>(), It.IsAny<Action<string>?>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .Returns((string _, IReadOnlyList<string> args, IReadOnlyDictionary<string, string>? _, Action<string>? onLine, bool _, CancellationToken _) =>
            {
                if (args.Contains("diff"))
                {
                    onLine?.Invoke($$"""{"message_type":"change","path":"/{{_bob.Id:D}}/Docs/new.txt","modifier":"+"}""");
                    return Task.FromResult(new ProcessResult(0, "", ""));
                }
                var dir = args[^1].TrimEnd('/');
                if (dir is HomesRoot or ProjectsRoot)
                {
                    Node(onLine, dir, _bob.Id.ToString("D"), "dir");
                    Node(onLine, dir, _orphan, "dir");
                    Node(onLine, dir, _zed.Id.ToString("D"), "dir");
                    Node(onLine, dir, _adam.Id.ToString("D"), "dir");
                }
                else
                    Node(onLine, dir, "Docs", "dir");
                return Task.FromResult(new ProcessResult(0, "", ""));
            });
    }

    private static void Node(Action<string>? onLine, string dir, string name, string type)
        => onLine?.Invoke($$"""{"name":"{{name}}","type":"{{type}}","path":"{{dir}}/{{name}}","message_type":"node"}""");

    public new void Dispose()
    {
        base.Dispose();
        try { Directory.Delete(_root, recursive: true); } catch (DirectoryNotFoundException) { }
    }

    private void AddPoint(ShareDefinition share, string root, string snapshotId, DateTime timeUtc)
    {
        using var db = NewContext();
        db.BackupSnapshots.Add(new BackupSnapshot
        {
            RepositoryId = _repo.Id,
            SnapshotId = snapshotId,
            Kind = BackupSnapshotKind.Share,
            ShareId = share.Id,
            TimeUtc = timeUtc,
            PathsJson = $"[\"{root}\",\"/meta/share-x/kaimo-acl-manifest.json\"]",
        });
        db.SaveChanges();
    }

    private async Task<BackupViewModel> LoadedAsync(ShareDefinition share)
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
        var vm = new BackupViewModel(AuthStateFor("admin"), UserContextFactoryFor(("admin", ContextFor(_admin))).Object, catalog,
            Mock.Of<IBackupRunner>(), Mock.Of<IDepartmentRepository>(), new BackupDownloadTokenService(new EphemeralDataProtectionProvider()),
            executor, new DemoModeOptions(), TimeProvider.System, NullLogger<BackupViewModel>.Instance, _users.Object);
        await vm.LoadAsync();
        Assert.True(await vm.SelectRestoreShareAsync(share.Id), vm.ErrorMessage);
        return vm;
    }

    [Fact]
    public async Task Home_folders_at_the_share_root_are_listed_by_their_owners_username()
    {
        var vm = await LoadedAsync(_homes);

        Assert.Contains(vm.BrowseEntries, e => e.Name == _bob.Id.ToString("D"));  // the real name is kept
        Assert.Equal("bob", vm.EntryLabel(_bob.Id.ToString("D")));
        Assert.Equal(_orphan, vm.EntryLabel(_orphan));                            // no owner: the GUID stays
    }

    [Fact]
    public async Task Inside_a_home_the_path_shows_the_username_but_entries_keep_their_names()
    {
        var vm = await LoadedAsync(_homes);
        Assert.True(await vm.OpenFolderAsync(_bob.Id.ToString("D")), vm.ErrorMessage);

        Assert.Equal(_bob.Id.ToString("D"), vm.BrowseFolder);                     // navigation keeps the GUID
        Assert.Equal("bob", vm.DisplayPath(vm.BrowseFolder));
        Assert.Equal("Docs", vm.EntryLabel("Docs"));
        Assert.Equal(_bob.Id.ToString("D"), vm.EntryLabel(_bob.Id.ToString("D"))); // only the share root holds homes
    }

    [Fact]
    public async Task Changed_paths_inside_a_home_show_the_username()
    {
        var vm = await LoadedAsync(_homes);
        Assert.True(await vm.ShowChangesAsync(true), vm.ErrorMessage);

        var change = Assert.Single(vm.Changes!.Items);
        Assert.Equal($"{_bob.Id:D}/Docs/new.txt", change.Path);
        Assert.Equal("bob/Docs/new.txt", vm.DisplayPath(change.Path));
    }

    private BackupRestoreDetails AddRestoreRun(string folder, IReadOnlyList<string> items, BackupRestoreOutcome? result,
        Guid? shareId = null)
    {
        var details = new BackupRestoreDetails(
            new BackupRestoreRequest(_repo.Id, "homes-new", shareId ?? _homes.Id, folder, items,
                BackupRestoreMode.NewFolder, BackupRestoreConflict.Skip, true),
            result);
        using var db = NewContext();
        db.BackupRuns.Add(new BackupRun
        {
            RepositoryId = _repo.Id,
            Type = BackupRunType.Restore,
            Status = result is null ? BackupRunStatus.Queued : BackupRunStatus.Succeeded,
            ActorUserId = _admin.Id,
            DetailsJson = details.Serialize(),
        });
        db.SaveChanges();
        return details;
    }

    [Fact]
    public async Task A_restore_run_names_the_home_it_restored_from_and_into_by_username()
    {
        var restore = AddRestoreRun($"{_bob.Id:D}/Docs", ["a.txt"], new BackupRestoreOutcome
        {
            SourceShareName = "users",
            DestinationShareId = _homes.Id,
            DestinationShareName = "users",
            DestinationPath = $"{_bob.Id:D}/Docs (restored)",
        });
        var vm = await LoadedAsync(_projects);                              // the activity log needs no homes selection

        Assert.Equal("bob/Docs", vm.RestoreSourceFolder(restore));
        Assert.Equal(["a.txt"], vm.RestoreItems(restore));
        Assert.Equal("bob/Docs (restored)", vm.RestoreTargetFolder(restore.Result!));
    }

    [Fact]
    public async Task Homes_restored_from_the_share_root_are_listed_by_username()
    {
        var restore = AddRestoreRun("", [_bob.Id.ToString("D"), _orphan], new BackupRestoreOutcome
        {
            SourceShareName = "users",
            DestinationShareName = "users",
        });
        var vm = await LoadedAsync(_projects);

        Assert.Equal("", vm.RestoreSourceFolder(restore));
        Assert.Equal(["bob", _orphan], vm.RestoreItems(restore));
    }

    [Fact]
    public async Task A_queued_restore_without_a_recorded_share_name_is_recognised_by_its_share()
    {
        var restore = AddRestoreRun($"{_bob.Id:D}/Docs", [], result: null);
        var vm = await LoadedAsync(_projects);

        Assert.Equal("bob/Docs", vm.RestoreSourceFolder(restore));
    }

    [Fact]
    public async Task A_restore_on_another_share_keeps_its_real_paths()
    {
        var restore = AddRestoreRun($"{_bob.Id:D}/Docs", [_bob.Id.ToString("D")], new BackupRestoreOutcome
        {
            SourceShareName = "projects",
            DestinationShareId = _projects.Id,
            DestinationShareName = "projects",
            DestinationPath = $"{_bob.Id:D}/Docs",
        }, _projects.Id);
        var vm = await LoadedAsync(_projects);

        Assert.Equal($"{_bob.Id:D}/Docs", vm.RestoreSourceFolder(restore));
        Assert.Equal([_bob.Id.ToString("D")], vm.RestoreItems(restore));
        Assert.Equal($"{_bob.Id:D}/Docs", vm.RestoreTargetFolder(restore.Result!));
    }

    [Fact]
    public async Task Other_shares_are_never_relabelled()
    {
        var vm = await LoadedAsync(_projects);

        Assert.Equal(_bob.Id.ToString("D"), vm.EntryLabel(_bob.Id.ToString("D")));
        Assert.Equal($"{_bob.Id:D}/Docs", vm.DisplayPath($"{_bob.Id:D}/Docs"));
    }

    [Fact]
    public async Task Home_folders_at_the_share_root_are_sorted_by_their_label()
    {
        var vm = await LoadedAsync(_homes);

        var labels = vm.BrowseEntries.Select(e => vm.EntryLabel(e.Name)).ToList();
        Assert.Equal([_orphan, "adam", "bob", "zed"], labels);
    }

    [Fact]
    public async Task A_typed_username_opens_that_users_home()
    {
        var vm = await LoadedAsync(_homes);

        Assert.True(await vm.OpenFolderAsync("BOB/Docs"), vm.ErrorMessage);

        Assert.Equal($"{_bob.Id:D}/Docs", vm.BrowseFolder);
    }

    [Fact]
    public async Task On_another_share_a_typed_username_is_kept_as_typed()
    {
        var vm = await LoadedAsync(_projects);

        Assert.True(await vm.OpenFolderAsync("bob"), vm.ErrorMessage);

        Assert.Equal("bob", vm.BrowseFolder);
    }

    [Fact]
    public async Task Owner_names_for_the_activity_log_are_reloaded_only_when_a_new_restore_run_shows_up()
    {
        AddRestoreRun($"{_bob.Id:D}/Docs", [], result: null);
        var vm = await LoadedAsync(_projects);
        _users.Verify(u => u.GetAllAsync(), Times.Once);

        await vm.ReloadAsync();
        _users.Verify(u => u.GetAllAsync(), Times.Once);

        var added = AddRestoreRun($"{_adam.Id:D}", [], result: null);
        await vm.ReloadAsync();
        _users.Verify(u => u.GetAllAsync(), Times.Exactly(2));
        Assert.Equal("adam", vm.RestoreSourceFolder(added));
    }

    [Fact]
    public async Task A_queued_restore_on_a_share_the_viewer_cannot_restore_still_names_the_home()
    {
        // The share is not among the viewer's restorable shares (e.g. a repository admin without
        // restore rights): only a home folder is named after a user id, so it is labelled anyway.
        var restore = AddRestoreRun($"{_bob.Id:D}/Docs", [], result: null, shareId: Guid.NewGuid());
        var vm = await LoadedAsync(_projects);

        Assert.Equal("bob/Docs", vm.RestoreSourceFolder(restore));
    }
}

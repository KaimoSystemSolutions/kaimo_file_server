using System.Security.Claims;
using Kaimo_File_Server.Core.Domain;
using Kaimo_File_Server.Core.Domain.Identity;
using Kaimo_File_Server.Core.Repositories.ExternalStorage;
using Kaimo_File_Server.Core.Repositories.Identity;
using Kaimo_File_Server.Core.Repositories.Shares;
using Kaimo_File_Server.Core.Security.Management;
using Kaimo_File_Server.Core.Services.CloudSync;
using Kaimo_File_Server.Core.Services.ExternalStorage;
using Kaimo_File_Server.Core.Services.File;
using Kaimo_File_Server.Core.Services.Management;
using Kaimo_File_Server.Infrastructure.ExternalStorage.CloudSync;
using Kaimo_File_Server.Web.Components.ViewModels;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Kaimo_File_Server.Web.Tests.ViewModels.ExternalStorage;

/// <summary>
/// The local folder of a sync on the home-folder share shows a home (named by its owner's GUID)
/// as the owner's username, like the file browser: in the folder picker, the path field and the
/// sync details. A typed username is accepted in place of the GUID; the stored path keeps the
/// GUID. Folders of other shares are never relabelled.
/// </summary>
public class ExternalStorageSyncViewModelHomeLabelsTests
{
    private readonly User _admin = new(Guid.NewGuid(), "Admin", "admin", "hash", "nt");
    private readonly User _bob = new(Guid.NewGuid(), "Bob", "bob", "hash", "nt");
    private readonly User _alice = new(Guid.NewGuid(), "Alice", "alice", "hash", "nt");
    private readonly ShareDefinition _homes = new("users", "/data/users", isEnabled: true) { IsUserHomes = true };
    private readonly ShareDefinition _projects = new("projects", "/data/projects", isEnabled: true);
    private readonly List<User> _users = [];
    private readonly Dictionary<(Guid Share, string Path), List<FileMetadata>> _listings = new();
    private readonly List<SyncDefinition> _definitions = [];
    private readonly Mock<ICloudSyncJobRunner> _jobRunner = new();

    private string BobHome => _bob.Id.ToString("D");
    private string AliceHome => _alice.Id.ToString("D");

    public ExternalStorageSyncViewModelHomeLabelsTests() => _users.AddRange([_admin, _bob, _alice]);

    private void Folders(ShareDefinition share, string path, params string[] names)
        => _listings[(share.Id, path)] = names
            .Select(name => new FileMetadata { Name = name, Path = path.Length == 0 ? name : $"{path}/{name}", IsDirectory = true })
            .ToList();

    private async Task<ExternalStorageSyncViewModel> LoadedAsync()
    {
        var identity = new ClaimsIdentity([new Claim(ClaimTypes.Name, _admin.Username)], "test");
        var authState = new Mock<AuthenticationStateProvider>();
        authState.Setup(a => a.GetAuthenticationStateAsync())
            .ReturnsAsync(new AuthenticationState(new ClaimsPrincipal(identity)));
        var contexts = new Mock<IUserContextFactory>();
        contexts.Setup(f => f.CreateByUsernameAsync(_admin.Username)).ReturnsAsync(new UserContext(_admin, [], [], []));

        var auth = new Mock<IManagementAuthService>();
        auth.Setup(a => a.GetAuthorizedShareIdsAnyAsync(It.IsAny<UserContext>(), It.IsAny<ManagementPermission>()))
            .ReturnsAsync(AuthorizedScopeResult.Unrestricted());
        auth.Setup(a => a.CanManageShareAsync(It.IsAny<UserContext>(), It.IsAny<Guid>(), It.IsAny<ManagementPermission>()))
            .ReturnsAsync(true);

        var shares = new Mock<IShareRepository>();
        shares.Setup(s => s.GetAllAsync()).ReturnsAsync([_homes, _projects]);
        shares.Setup(s => s.GetByIdAsync(_homes.Id)).ReturnsAsync(_homes);
        shares.Setup(s => s.GetByIdAsync(_projects.Id)).ReturnsAsync(_projects);

        var fileServices = new Mock<IFileServiceFactory>();
        foreach (var share in new[] { _homes, _projects })
        {
            var files = new Mock<IFileService>();
            files.Setup(f => f.ListAsync(It.IsAny<string>(), It.IsAny<UserContext>()))
                .ReturnsAsync((string path, UserContext _) => _listings.GetValueOrDefault((share.Id, path)) ?? []);
            fileServices.Setup(f => f.CreateForShare(share)).Returns(files.Object);
        }

        var syncs = new Mock<ISyncDefinitionRepository>();
        syncs.Setup(s => s.GetAllAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => _definitions.Select(d => new SyncDefinitionAdminEntry(d, null)).ToList());
        syncs.Setup(s => s.GetAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((Guid id, CancellationToken _) => _definitions.FirstOrDefault(d => d.Id == id));
        var connections = new Mock<IStorageConnectionRepository>();
        connections.Setup(c => c.GetAllAsync()).ReturnsAsync([]);
        var users = new Mock<IUserRepository>();
        users.Setup(u => u.GetAllAsync()).ReturnsAsync(() => _users.ToList());

        var vm = new ExternalStorageSyncViewModel(
            syncs.Object, connections.Object, shares.Object, Mock.Of<ILegacyCloudSyncMigrationService>(),
            auth.Object, contexts.Object, authState.Object, fileServices.Object, _jobRunner.Object,
            Mock.Of<IStorageConnectionProviderCatalog>(), Mock.Of<IStorageDirectoryTargetResolver>(),
            Mock.Of<ICloudSyncOperationCoordinator>(), new CloudSyncSchedulerSignal(), users.Object,
            NullLogger<ExternalStorageSyncViewModel>.Instance);
        await vm.LoadAsync();
        Assert.Null(vm.ErrorMessage);
        return vm;
    }

    // ── Folder picker ──

    [Fact]
    public async Task The_homes_at_the_share_root_are_listed_by_username_and_keep_their_real_path()
    {
        Folders(_homes, "", BobHome, AliceHome);
        var vm = await LoadedAsync();

        var items = await vm.ListLocalDirectoriesAsync(_homes.Id, "");

        Assert.Equal(["alice", "bob"], items.Select(i => i.Name));
        Assert.Equal([AliceHome, BobHome], items.Select(i => i.Path));
    }

    [Fact]
    public async Task A_home_folder_whose_owner_no_longer_exists_keeps_its_folder_name()
    {
        var orphan = Guid.NewGuid().ToString("D");
        Folders(_homes, "", BobHome, orphan);
        var vm = await LoadedAsync();

        var items = await vm.ListLocalDirectoriesAsync(_homes.Id, "");

        Assert.Contains(items, i => i.Name == orphan && i.Path == orphan);
        Assert.Contains(items, i => i.Name == "bob" && i.Path == BobHome);
    }

    [Fact]
    public async Task Folders_inside_a_home_keep_their_names()
    {
        Folders(_homes, BobHome, AliceHome, "docs");
        var vm = await LoadedAsync();

        var items = await vm.ListLocalDirectoriesAsync(_homes.Id, BobHome);

        Assert.Equal(new HashSet<string> { AliceHome, "docs" }, items.Select(i => i.Name).ToHashSet());
        Assert.All(items, i => Assert.StartsWith(BobHome + "/", i.Path));
    }

    [Fact]
    public async Task Folders_of_another_share_are_never_relabelled()
    {
        Folders(_projects, "", BobHome);
        var vm = await LoadedAsync();

        var items = await vm.ListLocalDirectoriesAsync(_projects.Id, "");

        Assert.Equal(BobHome, Assert.Single(items).Name);
    }

    [Fact]
    public async Task A_user_created_after_loading_shows_up_when_the_homes_root_is_listed()
    {
        var vm = await LoadedAsync();
        var carol = new User(Guid.NewGuid(), "Carol", "carol", "hash", "nt");
        _users.Add(carol);
        Folders(_homes, "", carol.Id.ToString("D"));

        var items = await vm.ListLocalDirectoriesAsync(_homes.Id, "");

        Assert.Equal("carol", Assert.Single(items).Name);
        Assert.Equal("/carol", vm.DisplayLocalPath(_homes.Id, carol.Id.ToString("D")));
    }

    // ── Shown path (path field, picker path and sync details) ──

    [Theory]
    [InlineData("", "/")]
    [InlineData("{bob}", "/bob")]
    [InlineData("{bob}/docs/2026", "/bob/docs/2026")]
    public async Task A_path_on_the_homes_share_is_shown_with_the_owners_username(string stored, string shown)
    {
        var vm = await LoadedAsync();

        Assert.Equal(shown, vm.DisplayLocalPath(_homes.Id, stored.Replace("{bob}", BobHome)));
    }

    [Fact]
    public async Task A_path_on_another_share_is_shown_as_stored()
    {
        var vm = await LoadedAsync();

        Assert.Equal("/", vm.DisplayLocalPath(_projects.Id, ""));
        Assert.Equal($"/{BobHome}/docs", vm.DisplayLocalPath(_projects.Id, $"{BobHome}/docs"));
    }

    [Fact]
    public async Task A_path_of_an_unknown_share_is_shown_as_stored()
    {
        var vm = await LoadedAsync();

        Assert.Equal($"/{BobHome}", vm.DisplayLocalPath(Guid.Empty, BobHome));
    }

    // ── Running Jobs menu ──

    [Fact]
    public async Task A_manual_run_of_a_home_folder_is_described_by_its_owners_username_while_the_job_keeps_the_stored_path()
    {
        var sync = new SyncDefinition
        {
            LocalShareId = _homes.Id, LocalPath = $"{BobHome}/docs", RemotePath = "/remote",
            Mode = SyncMode.Pull, DisplayName = "Bob's docs"
        };
        _definitions.Add(sync);
        var vm = await LoadedAsync();

        await vm.RunAsync(sync.Id);

        _jobRunner.Verify(r => r.Enqueue(_homes.Id, $"{BobHome}/docs", _admin.Id,
            It.Is<string>(title => title.EndsWith("Bob's docs")), "bob/docs ← /remote"), Times.Once);
    }

    [Fact]
    public async Task A_manual_run_of_an_unnamed_home_folder_sync_is_titled_with_the_owners_username()
    {
        var sync = new SyncDefinition { LocalShareId = _homes.Id, LocalPath = BobHome, RemotePath = "/remote" };
        _definitions.Add(sync);
        var vm = await LoadedAsync();

        await vm.RunAsync(sync.Id);

        _jobRunner.Verify(r => r.Enqueue(_homes.Id, BobHome, _admin.Id,
            It.Is<string>(title => title.EndsWith(": bob")), "bob ↔ /remote"), Times.Once);
    }

    [Fact]
    public async Task A_manual_run_on_another_share_is_described_as_stored()
    {
        var sync = new SyncDefinition
        {
            LocalShareId = _projects.Id, LocalPath = $"{BobHome}/docs", RemotePath = "/remote", DisplayName = "Docs"
        };
        _definitions.Add(sync);
        var vm = await LoadedAsync();

        await vm.RunAsync(sync.Id);

        _jobRunner.Verify(r => r.Enqueue(_projects.Id, $"{BobHome}/docs", _admin.Id,
            It.IsAny<string>(), $"{BobHome}/docs ↔ /remote"), Times.Once);
    }

    // ── Typed path ──

    [Theory]
    [InlineData("/bob/docs", "{bob}/docs")]
    [InlineData("\\Bob\\docs\\", "{bob}/docs")]
    [InlineData("bob", "{bob}")]
    [InlineData("/{bob}/docs", "{bob}/docs")]
    [InlineData("/nobody/docs", "nobody/docs")]
    [InlineData("/", "")]
    [InlineData("", "")]
    public async Task A_typed_username_on_the_homes_share_resolves_to_the_home_folder(string typed, string stored)
    {
        var vm = await LoadedAsync();

        Assert.Equal(stored.Replace("{bob}", BobHome), vm.ResolveLocalPath(_homes.Id, typed.Replace("{bob}", BobHome)));
    }

    [Fact]
    public async Task A_typed_path_on_another_share_is_only_normalized()
    {
        var vm = await LoadedAsync();

        Assert.Equal("bob/docs", vm.ResolveLocalPath(_projects.Id, "/bob/docs/"));
    }
}

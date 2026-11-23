using System.Security.Claims;
using Kaimo_File_Server.Core.Domain;
using Kaimo_File_Server.Core.Domain.Identity;
using Kaimo_File_Server.Core.Language;
using Kaimo_File_Server.Core.Repositories.ExternalStorage;
using Kaimo_File_Server.Core.Repositories.Identity;
using Kaimo_File_Server.Core.Repositories.Shares;
using Kaimo_File_Server.Core.Security.Demo;
using Kaimo_File_Server.Core.Security.Management;
using Kaimo_File_Server.Core.Services.File;
using Kaimo_File_Server.Core.Services.Management;
using Kaimo_File_Server.Core.Services.Search;
using Kaimo_File_Server.Infrastructure.Persistence;
using Kaimo_File_Server.Web.Components.ViewModels;
using Kaimo_File_Server.Web.Services.Downloads;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Kaimo_File_Server.Web.Tests.ViewModels.Files;

/// <summary>
/// The home-folder share in the regular file browser (<c>/files/users</c>): only holders of
/// <see cref="ManagementPermission.AccessHomeContents"/> may open it, home folders are shown and
/// addressable by their owner's username (the real path keeps the GUID), and administrative
/// features stay off.
/// </summary>
public class FileBrowserViewModelHomesTests
{
    private readonly Mock<IFileServiceFactory> _fileServiceFactory = new();
    private readonly Mock<IFileService> _fileService = new();
    private readonly Mock<IShareRepository> _shareRepo = new();
    private readonly Mock<IUserContextFactory> _userContextFactory = new();
    private readonly Mock<IManagementAuthService> _mgmtAuth = new();
    private readonly Mock<AuthenticationStateProvider> _authState = new();
    private readonly Mock<IUserRepository> _userRepo = new();
    private readonly Mock<ISyncDefinitionRepository> _syncRepo = new();

    private readonly ShareDefinition _homes = new("users", "/data/users", isEnabled: true) { IsUserHomes = true };
    private readonly User _alice = new(Guid.NewGuid(), "Alice Example", "alice", "hash", "nt");
    private readonly FileBrowserViewModel _sut;

    public FileBrowserViewModelHomesTests()
    {
        _shareRepo.Setup(r => r.GetByNameAsync("users")).ReturnsAsync(_homes);
        _fileServiceFactory.Setup(f => f.CreateForShare(It.IsAny<ShareDefinition>())).Returns(_fileService.Object);
        _fileService.Setup(s => s.ListAsync("", It.IsAny<UserContext>())).ReturnsAsync(
        [
            new FileMetadata { Name = _alice.Id.ToString("D"), Path = _alice.Id.ToString("D"), IsDirectory = true },
            new FileMetadata { Name = Guid.NewGuid().ToString("D"), Path = "orphan", IsDirectory = true },
            new FileMetadata { Name = "notes.txt", Path = "notes.txt" },
        ]);
        _fileService.Setup(s => s.ListAsync(_alice.Id.ToString("D"), It.IsAny<UserContext>())).ReturnsAsync(
            [new FileMetadata { Name = _alice.Id.ToString("D"), Path = $"{_alice.Id:D}/{_alice.Id:D}", IsDirectory = true }]);
        _userRepo.Setup(r => r.GetAllAsync()).ReturnsAsync([_alice]);

        var identity = new ClaimsIdentity(new[] { new Claim(ClaimTypes.Name, "admin") }, authenticationType: "test");
        _authState.Setup(a => a.GetAuthenticationStateAsync())
            .ReturnsAsync(new AuthenticationState(new ClaimsPrincipal(identity)));
        _userContextFactory.Setup(f => f.CreateByUsernameAsync("admin"))
            .ReturnsAsync(new UserContext(new User(Guid.NewGuid(), "Admin", "admin", "hash", "nt"), [], [], []));
        _syncRepo.Setup(r => r.GetAllAsync(It.IsAny<CancellationToken>())).ReturnsAsync(new List<SyncDefinitionAdminEntry>());
        var shareLinkRepo = new Mock<IShareLinkRepository>();
        shareLinkRepo.Setup(r => r.ListForSharesAsync(It.IsAny<IEnumerable<Guid>>())).ReturnsAsync(new List<ShareLink>());

        _sut = new FileBrowserViewModel(
            _fileServiceFactory.Object, _shareRepo.Object, Mock.Of<IDbContextFactory<ApplicationDbContext>>(),
            _userContextFactory.Object, _mgmtAuth.Object, _authState.Object,
            NullLogger<FileBrowserViewModel>.Instance, Mock.Of<ISearchService>(), _userRepo.Object,
            new FileDownloadTicketStore(), new ZipDownloadTicketStore(), new DemoModeOptions(), _syncRepo.Object,
            shareLinkRepo.Object);
    }

    private void AllowHomes(bool allowed) =>
        _mgmtAuth.Setup(m => m.HasAnyPermissionAsync(It.IsAny<UserContext>(), ManagementPermission.AccessHomeContents))
            .ReturnsAsync(allowed);

    [Fact]
    public async Task Without_the_permission_the_home_share_stays_unknown()
    {
        AllowHomes(false);

        Assert.False(await _sut.CanBrowseAllHomesAsync());
        await _sut.LoadShareAsync("users");

        Assert.Equal(Resources.Web_Error_ShareNotFound, _sut.ErrorMessage);
        Assert.Empty(_sut.Items);
        _fileService.Verify(s => s.ListAsync(It.IsAny<string>(), It.IsAny<UserContext>()), Times.Never);
    }

    [Fact]
    public async Task With_the_permission_the_home_share_is_browsable_like_a_share()
    {
        AllowHomes(true);

        Assert.True(await _sut.CanBrowseAllHomesAsync());
        await _sut.LoadShareAsync("users");

        Assert.Null(_sut.ErrorMessage);
        Assert.Equal(3, _sut.Items.Count);
        Assert.False(_sut.CanManageAcls);                    // home ACLs belong to the home-folder service
        Assert.False(_sut.Capabilities.HasCloudSync);
        Assert.False(_sut.Capabilities.HasFileAcls);
    }

    [Fact]
    public async Task Home_folders_at_the_share_root_show_the_owner_username_instead_of_the_guid()
    {
        AllowHomes(true);

        await _sut.LoadShareAsync("users");

        Assert.Equal("alice", _sut.DisplayNameOf(_sut.Items[0]));
        Assert.Equal(_sut.Items[1].Name, _sut.DisplayNameOf(_sut.Items[1]));   // no such user
        Assert.Equal("notes.txt", _sut.DisplayNameOf(_sut.Items[2]));          // not a home folder
    }

    [Fact]
    public async Task Home_folders_are_sorted_by_username()
    {
        AllowHomes(true);
        var zed = new User(Guid.Parse("00000000-0000-0000-0000-000000000001"), "Zed", "zed", "hash", "nt");
        var amy = new User(Guid.Parse("ffffffff-0000-0000-0000-000000000001"), "Amy", "amy", "hash", "nt");
        _userRepo.Setup(r => r.GetAllAsync()).ReturnsAsync([zed, amy]);
        _fileService.Setup(s => s.ListAsync("", It.IsAny<UserContext>())).ReturnsAsync(
        [
            new FileMetadata { Name = zed.Id.ToString("D"), Path = zed.Id.ToString("D"), IsDirectory = true },
            new FileMetadata { Name = amy.Id.ToString("D"), Path = amy.Id.ToString("D"), IsDirectory = true },
        ]);

        await _sut.LoadShareAsync("users");

        Assert.Equal(["amy", "zed"], _sut.Directories.Select(_sut.DisplayNameOf));
    }

    [Fact]
    public async Task Inside_a_home_the_breadcrumb_shows_the_username_but_the_path_keeps_the_guid()
    {
        AllowHomes(true);
        var home = _alice.Id.ToString("D");

        await _sut.LoadShareAsync("users", home);

        Assert.Equal(("alice", home), Assert.Single(_sut.Breadcrumbs));
        Assert.Equal(home, _sut.RouteSubPathOf(_sut.CurrentPath));   // the editable path bar text
    }

    [Theory]
    [InlineData("alice", "")]
    [InlineData("ALICE", "")]
    [InlineData("alice/docs", "/docs")]
    public async Task A_username_in_the_path_opens_that_users_home(string typed, string rest)
    {
        AllowHomes(true);
        _fileService.Setup(s => s.ListAsync(It.IsAny<string>(), It.IsAny<UserContext>())).ReturnsAsync([]);

        await _sut.LoadShareAsync("users", typed);

        Assert.Null(_sut.ErrorMessage);
        Assert.Equal($"{_alice.Id:D}{rest}", _sut.CurrentPath);
    }

    [Fact]
    public async Task An_unknown_name_in_the_path_is_not_resolved()
    {
        AllowHomes(true);
        _fileService.Setup(s => s.ListAsync(It.IsAny<string>(), It.IsAny<UserContext>())).ReturnsAsync([]);

        await _sut.LoadShareAsync("users", "nobody");

        Assert.Equal("nobody", _sut.CurrentPath);
    }

    [Fact]
    public async Task Usernames_are_not_resolved_or_shown_in_other_shares()
    {
        var projects = new ShareDefinition("projects", "/data/projects", isEnabled: true);
        var home = _alice.Id.ToString("D");
        _shareRepo.Setup(r => r.GetByNameAsync("projects")).ReturnsAsync(projects);
        _fileService.Setup(s => s.ListAsync(It.IsAny<string>(), It.IsAny<UserContext>())).ReturnsAsync(
            [new FileMetadata { Name = home, Path = home, IsDirectory = true }]);

        await _sut.LoadShareAsync("projects", "alice");
        Assert.Equal("alice", _sut.CurrentPath);

        await _sut.LoadShareAsync("projects");
        Assert.Equal(home, _sut.DisplayNameOf(Assert.Single(_sut.Items)));
    }

    [Fact]
    public async Task Overlapping_loads_do_not_lose_the_share_while_the_permission_check_awaits()
    {
        // Regression (Docker E2E): navigating inside /files/users loaded twice; the second load reset
        // CurrentShare while the first awaited the permission check → "share not found".
        var firstCheck = new TaskCompletionSource<bool>();
        var secondLookup = new TaskCompletionSource<ShareDefinition?>();
        _mgmtAuth.SetupSequence(m => m.HasAnyPermissionAsync(It.IsAny<UserContext>(), ManagementPermission.AccessHomeContents))
            .Returns(firstCheck.Task)
            .ReturnsAsync(true);
        _shareRepo.SetupSequence(r => r.GetByNameAsync("users"))
            .ReturnsAsync(_homes)
            .Returns(secondLookup.Task);

        var first = _sut.LoadShareAsync("users");
        var second = _sut.LoadShareAsync("users");
        firstCheck.SetResult(true);
        await first;
        secondLookup.SetResult(_homes);
        await second;

        Assert.Null(_sut.ErrorMessage);
        Assert.Same(_homes, _sut.CurrentShare);
    }

    [Fact]
    public async Task Only_folders_at_the_share_root_are_renamed_to_usernames()
    {
        AllowHomes(true);

        await _sut.LoadShareAsync("users", _alice.Id.ToString("D"));

        var inner = Assert.Single(_sut.Items);
        Assert.Equal(inner.Name, _sut.DisplayNameOf(inner));
    }
}

using System.Security.Claims;
using Kaimo_File_Server.Core.Domain;
using Kaimo_File_Server.Core.Domain.Identity;
using Kaimo_File_Server.Core.Repositories.Identity;
using Kaimo_File_Server.Core.Repositories.Shares;
using Kaimo_File_Server.Core.Services.File;
using Kaimo_File_Server.Core.Services.Management;
using Kaimo_File_Server.Web.Controllers.Api;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Moq;
using Xunit;

namespace Kaimo_File_Server.Web.Tests.Controllers.Api;

/// <summary>
/// <c>GET api/v1/browse/homes</c> names the GUID-named home folders by their owner's username, for
/// exactly the folders the caller may list at the root of the home-folder share, plus the caller's
/// own home.
/// </summary>
public sealed class HomeFoldersApiControllerTests
{
    private static User NewUser(string username) => new(Guid.NewGuid(), username, username, "hash", "nt");

    private readonly User _me = NewUser("admin.xyz");
    private readonly User _bob = NewUser("bob");
    private readonly User _amy = NewUser("amy");
    private readonly UserContext _user;
    private readonly ShareDefinition _homes = new("users", "/data/users", isEnabled: true) { IsUserHomes = true };
    private readonly ShareDefinition _docs = new("docs", "/data/docs", isEnabled: true);
    private readonly Mock<IShareRepository> _shares = new();
    private readonly Mock<IFileService> _fs = new();
    private readonly Mock<IUserContextFactory> _contexts = new();
    private readonly HomeFoldersApiController _sut;

    public HomeFoldersApiControllerTests()
    {
        _user = new UserContext(_me, [], [], []);
        _contexts.Setup(f => f.CreateByUserIdAsync(_me.Id)).ReturnsAsync(_user);
        _shares.Setup(r => r.GetAllAsync()).ReturnsAsync([_docs, _homes]);
        var factory = new Mock<IFileServiceFactory>();
        factory.Setup(f => f.CreateForShare(_homes)).Returns(_fs.Object);
        var users = new Mock<IUserRepository>();
        users.Setup(r => r.GetAllAsync()).ReturnsAsync([_me, _bob, _amy]);

        _sut = new HomeFoldersApiController(_contexts.Object, _shares.Object, factory.Object, users.Object)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext
                {
                    User = new ClaimsPrincipal(new ClaimsIdentity(
                        new[] { new Claim(ClaimTypes.NameIdentifier, _me.Id.ToString()) }, "test"))
                }
            }
        };
    }

    private static FileMetadata Dir(string name) => new() { Name = name, Path = name, IsDirectory = true };

    private static string FolderOf(User user) => user.Id.ToString("D");

    private void RootLists(params FileMetadata[] entries)
        => _fs.Setup(f => f.ListAsync("", _user)).ReturnsAsync(entries.ToList());

    private async Task<List<HomeFolderDto>> GetHomesAsync()
        => Assert.IsType<List<HomeFolderDto>>(Assert.IsType<OkObjectResult>(await _sut.GetHomes()).Value);

    [Fact]
    public async Task Every_listable_home_is_named_by_its_owner_ordered_by_username()
    {
        RootLists(Dir(FolderOf(_me)), Dir(FolderOf(_bob)), Dir(FolderOf(_amy)));

        var homes = await GetHomesAsync();

        Assert.Equal(
            [new(FolderOf(_me), "admin.xyz"), new(FolderOf(_amy), "amy"), new(FolderOf(_bob), "bob")],
            homes.OrderBy(h => h.Username, StringComparer.OrdinalIgnoreCase).ToList());
        Assert.Equal(["admin.xyz", "amy", "bob"], homes.Select(h => h.Username));
    }

    [Fact]
    public async Task Only_folders_the_caller_may_list_are_named()
    {
        // The ACL-filtered root listing decides: bob's home is not in it.
        RootLists(Dir(FolderOf(_me)), Dir(FolderOf(_amy)));

        var homes = await GetHomesAsync();

        Assert.DoesNotContain(homes, h => h.Username == "bob");
        Assert.Equal(2, homes.Count);
    }

    [Fact]
    public async Task Files_and_folders_without_an_owner_are_left_out()
    {
        RootLists(
            Dir(FolderOf(_me)),
            Dir(Guid.NewGuid().ToString("D")),
            new FileMetadata { Name = FolderOf(_bob), Path = FolderOf(_bob), IsDirectory = false });

        var home = Assert.Single(await GetHomesAsync());

        Assert.Equal(new HomeFolderDto(FolderOf(_me), "admin.xyz"), home);
    }

    [Fact]
    public async Task The_own_home_is_named_when_the_root_may_not_be_listed()
    {
        _fs.Setup(f => f.ListAsync("", _user)).ThrowsAsync(new UnauthorizedAccessException());

        var home = Assert.Single(await GetHomesAsync());

        Assert.Equal(new HomeFolderDto(FolderOf(_me), "admin.xyz"), home);
    }

    [Fact]
    public async Task The_own_home_is_named_once()
    {
        RootLists(Dir(FolderOf(_me).ToUpperInvariant()));

        var home = Assert.Single(await GetHomesAsync());

        Assert.Equal("admin.xyz", home.Username);
    }

    [Fact]
    public async Task A_disabled_own_home_is_not_named_unless_listed()
    {
        _me.HomeDirectoryEnabled = false;
        RootLists(Dir(FolderOf(_bob)));

        var home = Assert.Single(await GetHomesAsync());

        Assert.Equal(new HomeFolderDto(FolderOf(_bob), "bob"), home);
    }

    [Fact]
    public async Task Without_an_enabled_home_folder_share_nothing_is_named()
    {
        _shares.Setup(r => r.GetAllAsync()).ReturnsAsync([_docs]);
        Assert.Empty(await GetHomesAsync());

        _shares.Setup(r => r.GetAllAsync())
            .ReturnsAsync([new ShareDefinition("users", "/data/users", isEnabled: false) { IsUserHomes = true }]);
        Assert.Empty(await GetHomesAsync());
    }

    [Fact]
    public async Task An_unauthenticated_caller_is_refused()
    {
        _contexts.Setup(f => f.CreateByUserIdAsync(_me.Id)).ReturnsAsync((UserContext?)null);

        var result = Assert.IsAssignableFrom<ObjectResult>(await _sut.GetHomes());

        Assert.Equal(StatusCodes.Status401Unauthorized, result.StatusCode);
    }
}

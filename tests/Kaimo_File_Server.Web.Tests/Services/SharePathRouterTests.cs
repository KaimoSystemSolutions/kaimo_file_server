using System.Security.Claims;
using Kaimo_File_Server.Core.Domain;
using Kaimo_File_Server.Core.Domain.Identity;
using Kaimo_File_Server.Core.Repositories.Identity;
using Kaimo_File_Server.Core.Repositories.Shares;
using Kaimo_File_Server.Core.Security;
using Kaimo_File_Server.Core.Security.Acl;
using Kaimo_File_Server.Core.Security.Management;
using Kaimo_File_Server.Core.Services.Management;
using Kaimo_File_Server.Web.Services.CloudAccess;
using Kaimo_File_Server.Web.Services.Files;
using Microsoft.AspNetCore.Components.Authorization;
using Moq;
using Xunit;

namespace Kaimo_File_Server.Web.Tests.Services;

/// <summary>
/// A path typed after "/" in the path bar names a share the way the shares overview lists it: a
/// local share (any letter case), the own home ("user"), or a standalone virtual share by its name
/// (its route carries the id). Virtual folders are not shares; local names win.
/// </summary>
public class SharePathRouterTests
{
    private readonly Mock<IShareRepository> _shares = new();
    private readonly Mock<ICloudAccessRepository> _cloud = new();
    private readonly Mock<IManagementAuthService> _mgmt = new();
    private readonly Mock<IAclService> _acl = new();
    private readonly ShareDefinition _files = new("files", "/data/files", isEnabled: true);
    private readonly CloudAccessShare _standalone = new() { Name = "Cloud" };
    private readonly CloudAccessShare _mounted = new()
    {
        Name = "derWildeFolder", Provisioning = CloudAccessProvisioning.Folder, MountPath = "ffff/derWildeFolder",
    };
    private readonly SharePathRouter _sut;

    public SharePathRouterTests()
    {
        _mounted.MountShareId = _files.Id;
        _shares.Setup(r => r.GetAllAsync()).ReturnsAsync([_files]);
        _shares.Setup(r => r.GetByIdAsync(_files.Id)).ReturnsAsync(_files);
        _cloud.Setup(r => r.GetSharesAsync(It.IsAny<CancellationToken>())).ReturnsAsync([_standalone, _mounted]);
        _mgmt.Setup(m => m.GetAuthorizedDepartmentIdsAsync(It.IsAny<UserContext>(), It.IsAny<ManagementPermission>()))
            .ReturnsAsync(AuthorizedScopeResult.Unrestricted());
        _acl.Setup(a => a.HasAccessAsync(It.IsAny<UserContext>(), It.IsAny<Guid>(), It.IsAny<string>(), true, It.IsAny<FilePermission>()))
            .ReturnsAsync(true);

        var user = new User(Guid.NewGuid(), "Admin", "admin", "hash", "nt");
        var auth = new Mock<AuthenticationStateProvider>();
        auth.Setup(a => a.GetAuthenticationStateAsync()).ReturnsAsync(new AuthenticationState(
            new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.Name, "admin")], "test"))));
        var contexts = new Mock<IUserContextFactory>();
        contexts.Setup(f => f.CreateByUsernameAsync("admin")).ReturnsAsync(new UserContext(user, [], [], []));

        var authorization = new CloudAccessAuthorizationService(_cloud.Object, _mgmt.Object, _shares.Object, _acl.Object);
        _sut = new SharePathRouter(_shares.Object, authorization, auth.Object, contexts.Object);
    }

    [Theory]
    [InlineData("files", "files")]
    [InlineData("files/a b/c", "files/a%20b/c")]
    [InlineData("FILES/x", "files/x")]           // the share's real spelling
    [InlineData("user/docs", "user/docs")]
    [InlineData("User", "user")]
    public async Task Local_shares_and_the_own_home(string typed, string route)
        => Assert.Equal(route, await _sut.RouteOfAsync(typed));

    [Theory]
    [InlineData("Cloud", "")]
    [InlineData("cloud/a/b", "/a/b")]
    public async Task A_standalone_virtual_share_opens_by_its_name(string typed, string rest)
        => Assert.Equal($"access/{_standalone.Id}{rest}", await _sut.RouteOfAsync(typed));

    [Fact]
    public async Task A_virtual_folder_is_not_a_share_and_is_not_resolved_by_name()
    {
        // Reached only through its host share ("/files/ffff/derWildeFolder"), like in the overview.
        Assert.Equal("derWildeFolder/34", await _sut.RouteOfAsync("derWildeFolder/34"));
        Assert.Equal("files/ffff/derWildeFolder/34", await _sut.RouteOfAsync("files/ffff/derWildeFolder/34"));
    }

    [Fact]
    public async Task A_local_share_wins_over_a_virtual_share_of_the_same_name()
    {
        _standalone.Name = "files";

        Assert.Equal("files/x", await _sut.RouteOfAsync("files/x"));
    }

    [Fact]
    public async Task A_virtual_share_the_user_cannot_open_is_not_resolved()
    {
        _mgmt.Setup(m => m.GetAuthorizedDepartmentIdsAsync(It.IsAny<UserContext>(), It.IsAny<ManagementPermission>()))
            .ReturnsAsync(AuthorizedScopeResult.None());

        Assert.Equal("Cloud/x", await _sut.RouteOfAsync("Cloud/x"));
    }

    [Fact]
    public async Task An_unknown_name_is_left_as_typed()
        => Assert.Equal("nowhere/x", await _sut.RouteOfAsync("nowhere/x"));
}

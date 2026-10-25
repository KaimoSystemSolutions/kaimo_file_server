using System.Security.Claims;
using Kaimo_File_Server.Core.Domain;
using Kaimo_File_Server.Core.Domain.Identity;
using Kaimo_File_Server.Core.Repositories;
using Kaimo_File_Server.Core.Security;
using Kaimo_File_Server.Core.Services;
using Kaimo_File_Server.Core.Services.File;
using Kaimo_File_Server.Web.Controllers.Api;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Kaimo_File_Server.Tests;

/// <summary>
/// <c>GET api/v1/browse/shares</c> offers the whole home-folder share only to holders of
/// <see cref="ManagementPermission.AccessHomeContents"/>; home owners also hold a root list
/// right (for SMB), so the plain ACL check alone must not list it.
/// </summary>
public sealed class BrowseApiHomeSharesTests
{
    private readonly UserContext _user = new(new User(Guid.NewGuid(), "Test", "test", "hash", "nt"), [], [], []);
    private readonly ShareDefinition _homes = new("users", "/data/users", isEnabled: true) { IsUserHomes = true };
    private readonly Mock<IManagementAuthService> _mgmt = new();
    private readonly BrowseApiController _sut;

    public BrowseApiHomeSharesTests()
    {
        var users = new Mock<IUserContextFactory>();
        users.Setup(f => f.CreateByUserIdAsync(_user.User.Id)).ReturnsAsync(_user);
        var shares = new Mock<IShareRepository>();
        shares.Setup(r => r.GetAllEnabledAsync()).ReturnsAsync([_homes]);
        var fs = new Mock<IFileService>();
        fs.Setup(f => f.CanListAsync(string.Empty, _user)).ReturnsAsync(true);   // owner root entry
        var factory = new Mock<IFileServiceFactory>();
        factory.Setup(f => f.CreateForShare(It.IsAny<ShareDefinition>())).Returns(fs.Object);

        _sut = new BrowseApiController(users.Object, shares.Object, factory.Object, null!, _mgmt.Object,
            NullLogger<BrowseApiController>.Instance)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext
                {
                    User = new ClaimsPrincipal(new ClaimsIdentity(
                        new[] { new Claim(ClaimTypes.NameIdentifier, _user.User.Id.ToString()) }, "test"))
                }
            }
        };
    }

    private async Task<List<ShareDto>> GetSharesAsync()
        => Assert.IsType<List<ShareDto>>(Assert.IsType<OkObjectResult>(await _sut.GetShares()).Value);

    [Fact]
    public async Task Home_share_is_listed_for_holders_of_the_home_access_permission()
    {
        _mgmt.Setup(m => m.HasAnyPermissionAsync(_user, ManagementPermission.AccessHomeContents)).ReturnsAsync(true);

        var dto = Assert.Single(await GetSharesAsync());

        Assert.Equal(_homes.Id, dto.Id);
        Assert.Equal("users", dto.Name);
        Assert.Null(dto.RootPath);
    }

    [Fact]
    public async Task Home_share_is_not_listed_for_plain_home_owners()
        => Assert.Empty(await GetSharesAsync());
}

using System.Security.Claims;
using Kaimo_File_Server.Core.Domain;
using Kaimo_File_Server.Core.Domain.Identity;
using Kaimo_File_Server.Core.Repositories;
using Kaimo_File_Server.Core.Security;
using Kaimo_File_Server.Core.Services;
using Kaimo_File_Server.Infrastructure.Clouds;
using Kaimo_File_Server.Web.Controllers;
using Kaimo_File_Server.Web.Services;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Moq;
using Xunit;

namespace Kaimo_File_Server.Tests;

public sealed class GoogleOAuthControllerTests
{
    [Fact]
    public async Task Connect_RechecksPermissionBeforeStartingProviderAuthorization()
    {
        var share = new ShareDefinition("documents", "/data/documents", Guid.NewGuid());
        var user = new User(
            Guid.NewGuid(), "Test User", "test-user", "password-hash", "nt-hash");
        var actor = new UserContext(user, [], [], []);
        var shares = new Mock<IShareRepository>();
        shares.Setup(repository => repository.GetByIdAsync(share.Id)).ReturnsAsync(share);
        var users = new Mock<IUserContextFactory>();
        users.Setup(factory => factory.CreateByUsernameAsync(user.Username)).ReturnsAsync(actor);
        var authorization = new Mock<IManagementAuthService>();
        authorization.Setup(service => service.CanManageShareAsync(
                actor, share.Id, ManagementPermission.CreateSyncs))
            .ReturnsAsync(false);
        var identity = GoogleIdentityConfiguration.FromConfiguration(
            new ConfigurationBuilder().Build());
        var googleOAuth = new GoogleOAuthService(
            identity,
            new GoogleOAuthClientFactory(identity),
            new EphemeralDataProtectionProvider());
        var tickets = new Mock<ICloudAuthorizationTicketStore>(MockBehavior.Strict);
        var controller = new GoogleOAuthController(
            shares.Object,
            tickets.Object,
            users.Object,
            authorization.Object,
            googleOAuth,
            identity)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext
                {
                    User = new ClaimsPrincipal(new ClaimsIdentity(
                        [new Claim(ClaimTypes.Name, user.Username)], "test"))
                }
            }
        };

        var result = await controller.Connect(share.Id, "", "ticket");

        Assert.IsType<ForbidResult>(result);
        tickets.VerifyNoOtherCalls();
    }
}

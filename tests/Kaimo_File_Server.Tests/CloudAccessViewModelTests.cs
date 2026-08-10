using System.Security.Claims;
using Kaimo_File_Server.Core.Domain;
using Kaimo_File_Server.Core.Domain.Identity;
using Kaimo_File_Server.Core.Language;
using Kaimo_File_Server.Core.Repositories;
using Kaimo_File_Server.Core.Security;
using Kaimo_File_Server.Core.Services;
using Kaimo_File_Server.Web.Components.ViewModels;
using Kaimo_File_Server.Web.Services;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Kaimo_File_Server.Tests;

public sealed class CloudAccessViewModelTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task CreateShareAsync_RejectsNamesUsedByLocalOrVirtualShares(bool localCollision)
    {
        var departmentId = Guid.NewGuid();
        var user = new User(Guid.NewGuid(), "Alice", "alice", "hash", "nt");
        var actor = new UserContext(user, [], [], []);
        var connection = new CloudAccessConnection
        {
            DepartmentId = departmentId,
            State = CloudAccessConnectionState.Ready,
            ProtectedCredentials = "protected"
        };
        var cloudRepository = new Mock<ICloudAccessRepository>();
        cloudRepository.Setup(x => x.GetConnectionAsync(connection.Id, default)).ReturnsAsync(connection);
        cloudRepository.Setup(x => x.GetSharesAsync(default)).ReturnsAsync(localCollision
            ? []
            : [new CloudAccessShare { Name = "Documents" }]);
        var localRepository = new Mock<IShareRepository>();
        localRepository.Setup(x => x.GetAllAsync()).ReturnsAsync(localCollision
            ? [new ShareDefinition("documents", "C:/storage/documents")]
            : []);
        var management = new Mock<IManagementAuthService>();
        management.Setup(x => x.CanManageDepartmentAsync(
                actor, departmentId, ManagementPermission.ManageCloudAccess))
            .ReturnsAsync(true);
        var userContexts = new Mock<IUserContextFactory>();
        userContexts.Setup(x => x.CreateByUsernameAsync("alice")).ReturnsAsync(actor);
        var identity = new ClaimsIdentity(
            [new Claim(ClaimTypes.Name, "alice")], "test");
        var authentication = new Mock<AuthenticationStateProvider>();
        authentication.Setup(x => x.GetAuthenticationStateAsync())
            .ReturnsAsync(new AuthenticationState(new ClaimsPrincipal(identity)));
        var protector = new Mock<ICloudAccessCredentialProtector>();
        var viewModel = new CloudAccessViewModel(
            cloudRepository.Object,
            protector.Object,
            Mock.Of<ICloudAuthorizationTicketStore>(),
            management.Object,
            userContexts.Object,
            authentication.Object,
            Mock.Of<IDepartmentRepository>(),
            Mock.Of<IUserRepository>(),
            Mock.Of<IGroupRepository>(),
            localRepository.Object,
            Mock.Of<IHttpClientFactory>(),
            NullLogger<CloudAccessViewModel>.Instance);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            viewModel.CreateShareAsync(connection.Id, "Documents", "/", false, []));

        Assert.Equal(
            Resources.ResourceManager.GetString("Web_CloudAccess_ShareNameExists"),
            exception.Message);
        protector.Verify(x => x.Unprotect(It.IsAny<string>()), Times.Never);
        cloudRepository.Verify(x => x.UpsertShareAsync(
            It.IsAny<CloudAccessShare>(), It.IsAny<CancellationToken>()), Times.Never);
    }
}

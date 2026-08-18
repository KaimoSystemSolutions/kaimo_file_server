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
    [Fact]
    public async Task UpdateConnectionAsync_TrimsAndPersistsManagedConnectionName()
    {
        var departmentId = Guid.NewGuid();
        var user = new User(Guid.NewGuid(), "Alice", "alice", "hash", "nt");
        var actor = new UserContext(user, [], [], []);
        var connection = new StorageConnection { DepartmentId = departmentId, Name = "Old name" };
        var cloudRepository = new Mock<ICloudAccessRepository>();
        var connectionRepository = new Mock<IStorageConnectionRepository>();
        connectionRepository.Setup(x => x.GetAsync(connection.Id, default)).ReturnsAsync(connection);
        connectionRepository.Setup(x => x.GetAllAsync(default)).ReturnsAsync([connection]);
        cloudRepository.Setup(x => x.GetSharesAsync(default)).ReturnsAsync([]);
        var management = new Mock<IManagementAuthService>();
        management.Setup(x => x.CanManageDepartmentAsync(actor, departmentId, ManagementPermission.ManageConnections))
            .ReturnsAsync(true);
        management.Setup(x => x.GetAuthorizedDepartmentIdsAsync(actor, It.IsAny<ManagementPermission>()))
            .ReturnsAsync(AuthorizedScopeResult.Unrestricted());
        var userContexts = new Mock<IUserContextFactory>();
        userContexts.Setup(x => x.CreateByUsernameAsync("alice")).ReturnsAsync(actor);
        var authentication = new Mock<AuthenticationStateProvider>();
        authentication.Setup(x => x.GetAuthenticationStateAsync()).ReturnsAsync(new AuthenticationState(
            new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.Name, "alice")], "test"))));
        var departments = new Mock<IDepartmentRepository>();
        departments.Setup(x => x.GetAllAsync()).ReturnsAsync([]);
        var viewModel = new CloudAccessViewModel(
            cloudRepository.Object, connectionRepository.Object,
            Mock.Of<ICloudAuthorizationTicketStore>(),
            management.Object, userContexts.Object, authentication.Object, departments.Object,
            Mock.Of<IUserRepository>(), Mock.Of<IGroupRepository>(), Mock.Of<IShareRepository>(),
            ConnectionFactory(connectionRepository.Object, Mock.Of<ICredentialVault>()),
            NullLogger<CloudAccessViewModel>.Instance);

        await viewModel.UpdateConnectionAsync(connection.Id, "  Finance OneDrive  ");

        Assert.Equal("Finance OneDrive", connection.Name);
        connectionRepository.Verify(x => x.SaveAsync(connection, default), Times.Once);
    }

    [Fact]
    public async Task UpdateConnectionAsync_RejectsBlankNames()
    {
        var connection = new StorageConnection { DepartmentId = Guid.NewGuid(), Name = "Current" };
        var cloudRepository = new Mock<ICloudAccessRepository>();
        var connectionRepository = new Mock<IStorageConnectionRepository>();
        connectionRepository.Setup(x => x.GetAsync(connection.Id, default)).ReturnsAsync(connection);
        var management = new Mock<IManagementAuthService>();
        management.Setup(x => x.CanManageDepartmentAsync(It.IsAny<UserContext>(), connection.DepartmentId,
                ManagementPermission.ManageConnections)).ReturnsAsync(true);
        var user = new User(Guid.NewGuid(), "Alice", "alice", "hash", "nt");
        var contexts = new Mock<IUserContextFactory>();
        contexts.Setup(x => x.CreateByUsernameAsync("alice")).ReturnsAsync(new UserContext(user, [], [], []));
        var authentication = new Mock<AuthenticationStateProvider>();
        authentication.Setup(x => x.GetAuthenticationStateAsync()).ReturnsAsync(new AuthenticationState(
            new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.Name, "alice")], "test"))));
        var viewModel = new CloudAccessViewModel(
            cloudRepository.Object, connectionRepository.Object,
            Mock.Of<ICloudAuthorizationTicketStore>(),
            management.Object, contexts.Object, authentication.Object, Mock.Of<IDepartmentRepository>(),
            Mock.Of<IUserRepository>(), Mock.Of<IGroupRepository>(), Mock.Of<IShareRepository>(),
            ConnectionFactory(connectionRepository.Object, Mock.Of<ICredentialVault>()),
            NullLogger<CloudAccessViewModel>.Instance);

        var exception = await Assert.ThrowsAsync<ArgumentException>(() => viewModel.UpdateConnectionAsync(connection.Id, " "));

        Assert.Equal(Resources.ResourceManager.GetString("Web_CloudAccess_InvalidConnectionName"), exception.Message);
        connectionRepository.Verify(x => x.SaveAsync(It.IsAny<StorageConnection>(), default), Times.Never);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task CreateShareAsync_RejectsNamesUsedByLocalOrVirtualShares(bool localCollision)
    {
        var departmentId = Guid.NewGuid();
        var user = new User(Guid.NewGuid(), "Alice", "alice", "hash", "nt");
        var actor = new UserContext(user, [], [], []);
        var connection = new StorageConnection
        {
            DepartmentId = departmentId,
            ProviderId = "onedrive",
            State = StorageConnectionState.Ready,
            EncryptedCredentialPayload = "protected"
        };
        var cloudRepository = new Mock<ICloudAccessRepository>();
        var connectionRepository = new Mock<IStorageConnectionRepository>();
        connectionRepository.Setup(x => x.GetAsync(connection.Id, default)).ReturnsAsync(connection);
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
        management.Setup(x => x.CanManageDepartmentAsync(
                actor, departmentId, ManagementPermission.UseConnections))
            .ReturnsAsync(true);
        var userContexts = new Mock<IUserContextFactory>();
        userContexts.Setup(x => x.CreateByUsernameAsync("alice")).ReturnsAsync(actor);
        var identity = new ClaimsIdentity(
            [new Claim(ClaimTypes.Name, "alice")], "test");
        var authentication = new Mock<AuthenticationStateProvider>();
        authentication.Setup(x => x.GetAuthenticationStateAsync())
            .ReturnsAsync(new AuthenticationState(new ClaimsPrincipal(identity)));
        var credentialVault = new Mock<ICredentialVault>();
        var viewModel = new CloudAccessViewModel(
            cloudRepository.Object,
            connectionRepository.Object,
            Mock.Of<ICloudAuthorizationTicketStore>(),
            management.Object,
            userContexts.Object,
            authentication.Object,
            Mock.Of<IDepartmentRepository>(),
            Mock.Of<IUserRepository>(),
            Mock.Of<IGroupRepository>(),
            localRepository.Object,
            ConnectionFactory(connectionRepository.Object, credentialVault.Object),
            NullLogger<CloudAccessViewModel>.Instance);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            viewModel.CreateShareAsync(connection.Id, "Documents", "/", false, []));

        Assert.Equal(
            Resources.ResourceManager.GetString("Web_CloudAccess_ShareNameExists"),
            exception.Message);
        credentialVault.Verify(x => x.Unprotect<Dictionary<string, string>>(
            It.IsAny<string>(), It.IsAny<CredentialContext>()), Times.Never);
        cloudRepository.Verify(x => x.UpsertShareAsync(
            It.IsAny<CloudAccessShare>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    private static OneDriveStorageConnectionFactory ConnectionFactory(
        IStorageConnectionRepository repository,
        ICredentialVault vault)
        => new(
            repository,
            vault,
            Mock.Of<IStorageConnectionCredentialLeaseManager>(),
            Mock.Of<IHttpClientFactory>());
}

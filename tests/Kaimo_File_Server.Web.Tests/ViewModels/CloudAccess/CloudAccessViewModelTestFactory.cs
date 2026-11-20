using System.Security.Claims;
using Kaimo_File_Server.Core.Domain;
using Kaimo_File_Server.Core.Domain.Department;
using Kaimo_File_Server.Core.Domain.Identity;
using Kaimo_File_Server.Core.Repositories.ExternalStorage;
using Kaimo_File_Server.Core.Repositories.Identity;
using Kaimo_File_Server.Core.Repositories.Shares;
using Kaimo_File_Server.Core.Security.Credentials;
using Kaimo_File_Server.Core.Security.Management;
using Kaimo_File_Server.Core.Services.ExternalStorage;
using Kaimo_File_Server.Core.Services.Management;
using Kaimo_File_Server.Infrastructure.ExternalStorage;
using Kaimo_File_Server.Web.Components.ViewModels;
using Kaimo_File_Server.Web.Services.ExternalStorage;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace Kaimo_File_Server.Web.Tests.ViewModels.CloudAccess;

/// <summary>
/// Builds a <see cref="CloudAccessViewModel"/> for a global connection administrator with
/// in-memory SMB connections and one mocked SMB provider, for connection-test scenarios.
/// </summary>
internal static class CloudAccessViewModelTestFactory
{
    public static StorageConnection SmbConnection(string name = "NAS") => new()
    {
        ProviderId = "smb",
        Name = name,
        AuthorizationMode = StorageAuthorizationMode.UsernamePassword,
        State = StorageConnectionState.Ready,
        EncryptedCredentialPayload = "protected"
    };

    public static (CloudAccessViewModel ViewModel, Mock<IStorageConnectionProvider> Provider,
        List<StorageConnection> Saved) Create(params StorageConnection[] connections)
    {
        var user = new User(Guid.NewGuid(), "Alice", "alice", "hash", "nt");
        var actor = new UserContext(user, [], [], []);
        var saved = new List<StorageConnection>();
        var connectionRepository = new Mock<IStorageConnectionRepository>();
        connectionRepository.Setup(x => x.GetAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((Guid id, CancellationToken _) => connections.FirstOrDefault(c => c.Id == id));
        connectionRepository.Setup(x => x.GetAllAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => connections.ToList());
        connectionRepository.Setup(x => x.GetUsageAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new StorageConnectionUsage(0, 0));
        connectionRepository.Setup(x => x.SaveAsync(It.IsAny<StorageConnection>(), It.IsAny<CancellationToken>()))
            .Callback<StorageConnection, CancellationToken>((item, _) => saved.Add(item))
            .Returns(Task.CompletedTask);
        var cloudRepository = new Mock<ICloudAccessRepository>();
        cloudRepository.Setup(x => x.GetSharesAsync(It.IsAny<CancellationToken>())).ReturnsAsync([]);
        var management = new Mock<IManagementAuthService>();
        management.Setup(x => x.HasGlobalPermissionAsync(It.IsAny<UserContext>(), It.IsAny<ManagementPermission>()))
            .ReturnsAsync(true);
        management.Setup(x => x.GetAuthorizedDepartmentIdsAsync(actor, It.IsAny<ManagementPermission>()))
            .ReturnsAsync(AuthorizedScopeResult.Unrestricted());
        var userContexts = new Mock<IUserContextFactory>();
        userContexts.Setup(x => x.CreateByUsernameAsync("alice")).ReturnsAsync(actor);
        var authentication = new Mock<AuthenticationStateProvider>();
        authentication.Setup(x => x.GetAuthenticationStateAsync()).ReturnsAsync(new AuthenticationState(
            new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.Name, "alice")], "test"))));
        var departments = new Mock<IDepartmentRepository>();
        departments.Setup(x => x.GetAllAsync()).ReturnsAsync(new List<Department>());
        var vault = Mock.Of<ICredentialVault>();
        var provider = new Mock<IStorageConnectionProvider>();
        provider.SetupGet(x => x.Id).Returns("smb");
        provider.SetupGet(x => x.DisplayName).Returns("SMB");
        provider.SetupGet(x => x.AuthorizationModes).Returns(new HashSet<StorageAuthorizationMode>
            { StorageAuthorizationMode.UsernamePassword });
        var viewModel = new CloudAccessViewModel(
            cloudRepository.Object, connectionRepository.Object, Mock.Of<ICloudAuthorizationTicketStore>(),
            management.Object, userContexts.Object, authentication.Object, departments.Object,
            Mock.Of<IUserRepository>(), Mock.Of<IGroupRepository>(), Mock.Of<IShareRepository>(),
            new OneDriveStorageConnectionFactory(
                connectionRepository.Object, vault,
                Mock.Of<IStorageConnectionCredentialLeaseManager>(), Mock.Of<IHttpClientFactory>()),
            NullLogger<CloudAccessViewModel>.Instance,
            new StorageConnectionProviderCatalog([provider.Object]), vault);
        return (viewModel, provider, saved);
    }
}

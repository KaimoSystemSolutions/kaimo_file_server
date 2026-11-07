using System.Security.Claims;
using Kaimo_File_Server.Core.Domain;
using Kaimo_File_Server.Core.Domain.Identity;
using Kaimo_File_Server.Core.Language;
using Kaimo_File_Server.Core.Repositories;
using Kaimo_File_Server.Core.Security;
using Kaimo_File_Server.Core.Services;
using Kaimo_File_Server.Core.Services.ExternalStorage;
using Kaimo_File_Server.Infrastructure.ExternalStorage;
using Kaimo_File_Server.Web.Components.ViewModels;
using Kaimo_File_Server.Web.Services;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Kaimo_File_Server.Web.Tests.ViewModels.CloudAccess;

/// <summary>
/// Creating a virtual share directly as a folder inside a local share: the share is stored in
/// folder mode (never exposed standalone), mounted, and removed again when the mount is rejected.
/// </summary>
public sealed class CloudAccessViewModelCreateMountTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "kaimo-create-mount-" + Guid.NewGuid().ToString("N"));
    private readonly Guid _departmentId = Guid.NewGuid();
    private readonly UserContext _actor = new(new User(Guid.NewGuid(), "Alice", "alice", "hash", "nt"), [], [], []);
    private readonly StorageConnection _connection = new()
    {
        ProviderId = "onedrive", State = StorageConnectionState.Ready, EncryptedCredentialPayload = "protected"
    };
    private readonly Mock<ICloudAccessRepository> _repository = new();
    private readonly Mock<IManagementAuthService> _management = new();
    private readonly Mock<IShareRepository> _localShares = new();
    private readonly ShareDefinition _host;
    private CloudAccessProvisioning? _provisioningAtInsert;
    private CloudAccessShare? _inserted;

    public CloudAccessViewModelCreateMountTests()
    {
        Directory.CreateDirectory(Path.Combine(_root, "Team", "Existing"));
        _host = new ShareDefinition("projekte", _root, null, true, false, false);
        _localShares.Setup(x => x.GetAllAsync()).ReturnsAsync([_host]);
        _localShares.Setup(x => x.GetByIdAsync(_host.Id)).ReturnsAsync(_host);
        _repository.Setup(x => x.GetSharesAsync(default)).ReturnsAsync([]);
        _repository.Setup(x => x.UpsertShareAsync(It.IsAny<CloudAccessShare>(), default))
            .Callback<CloudAccessShare, CancellationToken>((share, _) =>
            {
                _inserted = share;
                _provisioningAtInsert = share.Provisioning;
            })
            .Returns(Task.CompletedTask);
        _management.Setup(x => x.CanManageDepartmentAsync(_actor, _departmentId, ManagementPermission.ManageCloudAccess))
            .ReturnsAsync(true);
        _management.Setup(x => x.HasGlobalPermissionAsync(_actor, ManagementPermission.UseConnections)).ReturnsAsync(true);
        _management.Setup(x => x.GetAuthorizedDepartmentIdsAsync(_actor, It.IsAny<ManagementPermission>()))
            .ReturnsAsync(AuthorizedScopeResult.Unrestricted());
        _management.Setup(x => x.CanManageShareAsync(_actor, _host.Id, ManagementPermission.EditShareSettings))
            .ReturnsAsync(true);
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private CloudAccessViewModel Sut()
    {
        var connections = new Mock<IStorageConnectionRepository>();
        connections.Setup(x => x.GetAsync(_connection.Id, default)).ReturnsAsync(_connection);
        connections.Setup(x => x.GetAllAsync(default)).ReturnsAsync([]);
        var userContexts = new Mock<IUserContextFactory>();
        userContexts.Setup(x => x.CreateByUsernameAsync("alice")).ReturnsAsync(_actor);
        var authentication = new Mock<AuthenticationStateProvider>();
        authentication.Setup(x => x.GetAuthenticationStateAsync()).ReturnsAsync(new AuthenticationState(
            new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.Name, "alice")], "test"))));
        var departments = new Mock<IDepartmentRepository>();
        departments.Setup(x => x.GetAllAsync()).ReturnsAsync([]);
        var groups = new Mock<IGroupRepository>();
        groups.Setup(x => x.GetAllAsync()).ReturnsAsync([]);
        var directoryTargets = new Mock<IStorageDirectoryTargetResolver>();
        directoryTargets.Setup(x => x.ResolveDirectoryAsync(_connection, It.IsAny<string>(), default))
            .ReturnsAsync(new StorageDirectoryTarget("documents", "item-1"));
        var authorization = new CloudAccessAuthorizationService(_repository.Object, _management.Object);
        var mounts = new CloudAccessMountService(_repository.Object, _localShares.Object, authorization,
            _management.Object, NullLogger<CloudAccessMountService>.Instance);
        return new CloudAccessViewModel(
            _repository.Object, connections.Object, Mock.Of<ICloudAuthorizationTicketStore>(),
            _management.Object, userContexts.Object, authentication.Object, departments.Object,
            Mock.Of<IUserRepository>(), groups.Object, _localShares.Object,
            new OneDriveStorageConnectionFactory(connections.Object, Mock.Of<ICredentialVault>(),
                Mock.Of<IStorageConnectionCredentialLeaseManager>(), Mock.Of<IHttpClientFactory>()),
            NullLogger<CloudAccessViewModel>.Instance,
            directoryTargets: directoryTargets.Object,
            cloudAuthorization: authorization,
            mounts: mounts);
    }

    private Task Create(CloudAccessMountRequest? mount) => Sut().CreateShareAsync(
        _connection.Id, "Docs", "documents", _departmentId, new Dictionary<Guid, CloudAccessPermission>(), mount);

    [Fact]
    public async Task WithoutMountRequest_CreatesStandaloneVirtualShare()
    {
        await Create(null);

        Assert.Equal(CloudAccessProvisioning.VirtualShare, _provisioningAtInsert);
        _repository.Verify(x => x.SetMountAsync(It.IsAny<Guid>(), It.IsAny<CloudAccessProvisioning>(),
            It.IsAny<Guid?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task WithMountRequest_InsertsInFolderModeAndMounts()
    {
        await Create(new CloudAccessMountRequest(_host.Id, "Team", "Cloud"));

        // Stored in folder mode from the first write, so it is never reachable at /files/access/{id}.
        Assert.Equal(CloudAccessProvisioning.Folder, _provisioningAtInsert);
        _repository.Verify(x => x.SetMountAsync(_inserted!.Id, CloudAccessProvisioning.Folder, _host.Id,
            "Team/Cloud", It.IsAny<CancellationToken>()), Times.Once);
        _repository.Verify(x => x.DeleteShareAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task EmptyFolderName_FallsBackToShareName()
    {
        await Create(new CloudAccessMountRequest(_host.Id, "Team", "  "));

        _repository.Verify(x => x.SetMountAsync(_inserted!.Id, CloudAccessProvisioning.Folder, _host.Id,
            "Team/Docs", It.IsAny<CancellationToken>()), Times.Once);
    }

    [Theory]
    [InlineData("Team", "existing", "Web_CloudAccess_Mount_Error_NameInUse")]
    [InlineData("Missing", "Cloud", "Web_CloudAccess_Mount_Error_ParentMissing")]
    public async Task RejectedMount_DeletesTheNewShareAndReportsTheReason(string parent, string name, string errorKey)
    {
        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => Create(new CloudAccessMountRequest(_host.Id, parent, name)));

        Assert.Equal(Resources.ResourceManager.GetString(errorKey), error.Message);
        _repository.Verify(x => x.DeleteShareAsync(_inserted!.Id, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task HostWithoutEditRight_DeletesTheNewShare()
    {
        _management.Setup(x => x.CanManageShareAsync(_actor, _host.Id, ManagementPermission.EditShareSettings))
            .ReturnsAsync(false);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => Create(new CloudAccessMountRequest(_host.Id, "Team", "Cloud")));

        _repository.Verify(x => x.DeleteShareAsync(_inserted!.Id, It.IsAny<CancellationToken>()), Times.Once);
    }
}

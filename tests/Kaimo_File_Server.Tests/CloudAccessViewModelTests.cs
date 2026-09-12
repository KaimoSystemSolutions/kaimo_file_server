using System.Security.Claims;
using Kaimo_File_Server.Core.Domain;
using Kaimo_File_Server.Core.Domain.Department;
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

namespace Kaimo_File_Server.Tests;

public sealed class CloudAccessViewModelTests
{
    [Fact]
    public async Task UpdateConnectionAsync_TrimsAndPersistsManagedConnectionName()
    {
        var user = new User(Guid.NewGuid(), "Alice", "alice", "hash", "nt");
        var actor = new UserContext(user, [], [], []);
        var connection = new StorageConnection { Name = "Old name" };
        var cloudRepository = new Mock<ICloudAccessRepository>();
        var connectionRepository = new Mock<IStorageConnectionRepository>();
        connectionRepository.Setup(x => x.GetAsync(connection.Id, default)).ReturnsAsync(connection);
        connectionRepository.Setup(x => x.GetAllAsync(default)).ReturnsAsync([connection]);
        cloudRepository.Setup(x => x.GetSharesAsync(default)).ReturnsAsync([]);
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
        var connection = new StorageConnection { Name = "Current" };
        var cloudRepository = new Mock<ICloudAccessRepository>();
        var connectionRepository = new Mock<IStorageConnectionRepository>();
        connectionRepository.Setup(x => x.GetAsync(connection.Id, default)).ReturnsAsync(connection);
        var management = new Mock<IManagementAuthService>();
        management.Setup(x => x.HasGlobalPermissionAsync(It.IsAny<UserContext>(),
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
        management.Setup(x => x.HasGlobalPermissionAsync(actor, ManagementPermission.UseConnections))
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
            viewModel.CreateShareAsync(connection.Id, "Documents", "/", departmentId,
                new Dictionary<Guid, CloudAccessPermission>()));

        Assert.Equal(
            Resources.ResourceManager.GetString("Web_CloudAccess_ShareNameExists"),
            exception.Message);
        credentialVault.Verify(x => x.Unprotect<Dictionary<string, string>>(
            It.IsAny<string>(), It.IsAny<CredentialContext>()), Times.Never);
        cloudRepository.Verify(x => x.UpsertShareAsync(
            It.IsAny<CloudAccessShare>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task CreateConfiguredConnectionAsync_ProtectsManagedSshKeyOutsideSettingsJson()
    {
        var departmentId = Guid.NewGuid();
        var user = new User(Guid.NewGuid(), "Alice", "alice", "hash", "nt");
        var actor = new UserContext(user, [], [], []);
        var savedConnections = new List<StorageConnection>();
        var connectionRepository = new Mock<IStorageConnectionRepository>();
        connectionRepository.Setup(x => x.SaveAsync(It.IsAny<StorageConnection>(), default))
            .Callback<StorageConnection, CancellationToken>((connection, _) => savedConnections.Add(connection))
            .Returns(Task.CompletedTask);
        connectionRepository.Setup(x => x.GetAllAsync(default)).ReturnsAsync(() => savedConnections);
        connectionRepository.Setup(x => x.GetUsageAsync(It.IsAny<Guid>(), default))
            .ReturnsAsync(new StorageConnectionUsage(0, 0));
        var cloudRepository = new Mock<ICloudAccessRepository>();
        cloudRepository.Setup(x => x.GetSharesAsync(default)).ReturnsAsync([]);
        var management = new Mock<IManagementAuthService>();
        management.Setup(x => x.HasGlobalPermissionAsync(It.IsAny<UserContext>(), It.IsAny<ManagementPermission>()))
            .ReturnsAsync(true);
        management.Setup(x => x.GetAuthorizedDepartmentIdsAsync(actor, It.IsAny<ManagementPermission>()))
            .ReturnsAsync(AuthorizedScopeResult.Unrestricted());
        var contexts = new Mock<IUserContextFactory>();
        contexts.Setup(x => x.CreateByUsernameAsync("alice")).ReturnsAsync(actor);
        var authentication = new Mock<AuthenticationStateProvider>();
        authentication.Setup(x => x.GetAuthenticationStateAsync()).ReturnsAsync(new AuthenticationState(
            new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.Name, "alice")], "test"))));
        var departments = new Mock<IDepartmentRepository>();
        departments.Setup(x => x.GetAllAsync()).ReturnsAsync([new Department("Finance") { Id = departmentId }]);
        IReadOnlyDictionary<string, string>? protectedCredentials = null;
        var vault = new Mock<ICredentialVault>();
        vault.Setup(x => x.Protect(
                It.IsAny<IReadOnlyDictionary<string, string>>(), It.IsAny<CredentialContext>()))
            .Callback<IReadOnlyDictionary<string, string>, CredentialContext>((credentials, _) =>
                protectedCredentials = credentials)
            .Returns("protected-key");
        var provider = new Mock<IStorageConnectionProvider>();
        provider.SetupGet(x => x.Id).Returns("rsync-ssh");
        provider.SetupGet(x => x.AuthorizationModes).Returns(new HashSet<StorageAuthorizationMode>
            { StorageAuthorizationMode.SshKey });
        provider.Setup(x => x.TestAsync(It.IsAny<StorageConnection>(), default))
            .ReturnsAsync(new StorageConnectionHealthResult(
                StorageConnectionHealthState.Healthy, "ok", DateTime.UtcNow));
        var viewModel = new CloudAccessViewModel(
            cloudRepository.Object, connectionRepository.Object, Mock.Of<ICloudAuthorizationTicketStore>(),
            management.Object, contexts.Object, authentication.Object, departments.Object,
            Mock.Of<IUserRepository>(), Mock.Of<IGroupRepository>(), Mock.Of<IShareRepository>(),
            ConnectionFactory(connectionRepository.Object, vault.Object),
            NullLogger<CloudAccessViewModel>.Instance,
            new StorageConnectionProviderCatalog([provider.Object]), vault.Object);
        byte[] privateKey = "PRIVATE KEY CONTENT"u8.ToArray();
        const string settings = "{\"Host\":\"backup.example.com\",\"PrivateKeySecretReference\":\"\"}";

        await viewModel.CreateConfiguredConnectionAsync(
            "rsync-ssh", "Backup", settings, sshPrivateKey: privateKey);

        StorageConnection connection = Assert.Single(savedConnections);
        Assert.Equal("protected-key", connection.EncryptedCredentialPayload);
        Assert.DoesNotContain(Convert.ToBase64String(privateKey), connection.SettingsJson, StringComparison.Ordinal);
        Assert.Equal(Convert.ToBase64String(privateKey), protectedCredentials?["privateKeyBase64"]);
    }

    [Fact]
    public async Task UpdateConfiguredConnectionAsync_KeepsStoredPassword_WhenPasswordBlank()
    {
        var departmentId = Guid.NewGuid();
        var user = new User(Guid.NewGuid(), "Alice", "alice", "hash", "nt");
        var actor = new UserContext(user, [], [], []);
        var connection = new StorageConnection
        {
            ProviderId = "smb",
            Name = "Old name",
            AuthorizationMode = StorageAuthorizationMode.UsernamePassword,
            SettingsJson = "{\"Server\":\"old\"}",
            EncryptedCredentialPayload = "existing-payload",
            AccountDisplayName = "olduser"
        };
        var cloudRepository = new Mock<ICloudAccessRepository>();
        cloudRepository.Setup(x => x.GetSharesAsync(default)).ReturnsAsync([]);
        var connectionRepository = new Mock<IStorageConnectionRepository>();
        connectionRepository.Setup(x => x.GetAsync(connection.Id, default)).ReturnsAsync(connection);
        connectionRepository.Setup(x => x.GetAllAsync(default)).ReturnsAsync([connection]);
        connectionRepository.Setup(x => x.GetUsageAsync(It.IsAny<Guid>(), default))
            .ReturnsAsync(new StorageConnectionUsage(0, 0));
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
        departments.Setup(x => x.GetAllAsync()).ReturnsAsync([new Department("Finance") { Id = departmentId }]);
        IReadOnlyDictionary<string, string>? protectedCredentials = null;
        var vault = new Mock<ICredentialVault>();
        vault.Setup(x => x.Unprotect<Dictionary<string, string>>(
                "existing-payload", It.IsAny<CredentialContext>()))
            .Returns(new Dictionary<string, string> { ["username"] = "olduser", ["password"] = "secret" });
        vault.Setup(x => x.Protect(
                It.IsAny<IReadOnlyDictionary<string, string>>(), It.IsAny<CredentialContext>()))
            .Callback<IReadOnlyDictionary<string, string>, CredentialContext>((credentials, _) =>
                protectedCredentials = credentials)
            .Returns("reprotected");
        var provider = new Mock<IStorageConnectionProvider>();
        provider.SetupGet(x => x.Id).Returns("smb");
        provider.SetupGet(x => x.AuthorizationModes).Returns(new HashSet<StorageAuthorizationMode>
            { StorageAuthorizationMode.UsernamePassword });
        provider.Setup(x => x.TestAsync(It.IsAny<StorageConnection>(), default))
            .ReturnsAsync(new StorageConnectionHealthResult(
                StorageConnectionHealthState.Healthy, "ok", DateTime.UtcNow));
        var viewModel = new CloudAccessViewModel(
            cloudRepository.Object, connectionRepository.Object, Mock.Of<ICloudAuthorizationTicketStore>(),
            management.Object, userContexts.Object, authentication.Object, departments.Object,
            Mock.Of<IUserRepository>(), Mock.Of<IGroupRepository>(), Mock.Of<IShareRepository>(),
            ConnectionFactory(connectionRepository.Object, vault.Object),
            NullLogger<CloudAccessViewModel>.Instance,
            new StorageConnectionProviderCatalog([provider.Object]), vault.Object);

        await viewModel.UpdateConfiguredConnectionAsync(
            connection.Id, "  New name  ", "{\"Server\":\"new\"}", username: "newuser", password: "");

        Assert.Equal("New name", connection.Name);
        Assert.Equal("{\"Server\":\"new\"}", connection.SettingsJson);
        Assert.Equal("newuser", connection.AccountDisplayName);
        Assert.Equal("reprotected", connection.EncryptedCredentialPayload);
        Assert.Equal("newuser", protectedCredentials?["username"]);
        Assert.Equal("secret", protectedCredentials?["password"]);
    }

    [Fact]
    public async Task CreateShareAsync_GrantsCreatorAndAdminsGroupWrite()
    {
        var departmentId = Guid.NewGuid();
        var user = new User(Guid.NewGuid(), "Alice", "alice", "hash", "nt");
        var actor = new UserContext(user, [], [], []);
        var adminsGroup = new Group(Guid.NewGuid(), "Admins");
        var connection = new StorageConnection
        {
            ProviderId = "onedrive",
            State = StorageConnectionState.Ready,
            EncryptedCredentialPayload = "protected"
        };
        var cloudRepository = new Mock<ICloudAccessRepository>();
        cloudRepository.Setup(x => x.GetSharesAsync(default)).ReturnsAsync([]);
        List<CloudAccessGrant>? savedGrants = null;
        cloudRepository.Setup(x => x.ReplaceGrantsAsync(It.IsAny<Guid>(), It.IsAny<IEnumerable<CloudAccessGrant>>(), default))
            .Callback<Guid, IEnumerable<CloudAccessGrant>, CancellationToken>((_, grants, _) => savedGrants = grants.ToList())
            .Returns(Task.CompletedTask);
        var connectionRepository = new Mock<IStorageConnectionRepository>();
        connectionRepository.Setup(x => x.GetAsync(connection.Id, default)).ReturnsAsync(connection);
        connectionRepository.Setup(x => x.GetAllAsync(default)).ReturnsAsync([]);
        var management = new Mock<IManagementAuthService>();
        management.Setup(x => x.CanManageDepartmentAsync(actor, departmentId, ManagementPermission.ManageCloudAccess))
            .ReturnsAsync(true);
        management.Setup(x => x.HasGlobalPermissionAsync(actor, ManagementPermission.UseConnections)).ReturnsAsync(true);
        management.Setup(x => x.GetAuthorizedDepartmentIdsAsync(actor, It.IsAny<ManagementPermission>()))
            .ReturnsAsync(AuthorizedScopeResult.Unrestricted());
        var userContexts = new Mock<IUserContextFactory>();
        userContexts.Setup(x => x.CreateByUsernameAsync("alice")).ReturnsAsync(actor);
        var authentication = new Mock<AuthenticationStateProvider>();
        authentication.Setup(x => x.GetAuthenticationStateAsync()).ReturnsAsync(new AuthenticationState(
            new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.Name, "alice")], "test"))));
        var departments = new Mock<IDepartmentRepository>();
        departments.Setup(x => x.GetAllAsync()).ReturnsAsync([]);
        var groups = new Mock<IGroupRepository>();
        groups.Setup(x => x.GetAllAsync()).ReturnsAsync([adminsGroup]);
        var localRepository = new Mock<IShareRepository>();
        localRepository.Setup(x => x.GetAllAsync()).ReturnsAsync([]);
        var directoryTargets = new Mock<IStorageDirectoryTargetResolver>();
        directoryTargets.Setup(x => x.ResolveDirectoryAsync(connection, It.IsAny<string>(), default))
            .ReturnsAsync(new StorageDirectoryTarget("documents", "item-1"));
        var viewModel = new CloudAccessViewModel(
            cloudRepository.Object, connectionRepository.Object, Mock.Of<ICloudAuthorizationTicketStore>(),
            management.Object, userContexts.Object, authentication.Object, departments.Object,
            Mock.Of<IUserRepository>(), groups.Object, localRepository.Object,
            ConnectionFactory(connectionRepository.Object, Mock.Of<ICredentialVault>()),
            NullLogger<CloudAccessViewModel>.Instance,
            directoryTargets: directoryTargets.Object);

        await viewModel.CreateShareAsync(
            connection.Id, "Docs", "documents", departmentId,
            new Dictionary<Guid, CloudAccessPermission>());

        Assert.NotNull(savedGrants);
        Assert.Equal(CloudAccessPermission.Write, savedGrants!.Single(g => g.PrincipalId == user.Id).Permission);
        Assert.Equal(CloudAccessPermission.Write, savedGrants.Single(g => g.PrincipalId == adminsGroup.Id).Permission);
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

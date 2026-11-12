using System.Security.Claims;
using Kaimo_File_Server.Core.Domain;
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
using Kaimo_File_Server.Web.Services;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Kaimo_File_Server.Web.Tests.ViewModels.CloudAccess;

/// <summary>Picking the remote root of an SSH connection before it has been saved.</summary>
public sealed class CloudAccessViewModelDraftBrowseTests
{
    private const string OldSettings = "{\"Host\":\"old\",\"RemoteRoot\":\"/srv\"}";

    private readonly Mock<IStorageConnectionRepository> _connections = new();
    private readonly Mock<IManagementAuthService> _management = new();
    private readonly Mock<IStorageDirectoryTargetResolver> _targets = new();
    private readonly Mock<ICredentialVault> _vault = new();
    private readonly StorageConnection _stored = new()
    {
        ProviderId = "sftp",
        AuthorizationMode = StorageAuthorizationMode.UsernamePassword,
        SettingsJson = OldSettings,
        EncryptedCredentialPayload = "stored-payload"
    };
    private StorageConnection? _browsed;
    private IReadOnlyDictionary<string, string>? _protected;
    private CredentialContext? _protectedContext;

    public CloudAccessViewModelDraftBrowseTests()
    {
        _connections.Setup(x => x.GetAsync(_stored.Id, default)).ReturnsAsync(_stored);
        _management.Setup(x => x.HasGlobalPermissionAsync(It.IsAny<UserContext>(), It.IsAny<ManagementPermission>()))
            .ReturnsAsync(true);
        _targets.Setup(x => x.ListDirectoriesAsync(It.IsAny<StorageConnection>(), "/data", default))
            .Callback<StorageConnection, string, CancellationToken>((connection, _, _) => _browsed = connection)
            .ReturnsAsync([new RemoteStorageItem("child", "/data/child", true, null, null)]);
        _vault.Setup(x => x.Protect(It.IsAny<IReadOnlyDictionary<string, string>>(), It.IsAny<CredentialContext>()))
            .Callback<IReadOnlyDictionary<string, string>, CredentialContext>((credentials, context) =>
            {
                _protected = credentials;
                _protectedContext = context;
            })
            .Returns("draft-payload");
    }

    [Fact]
    public async Task NewConnection_BrowsesWithTransientCredentialsAndSavesNothing()
    {
        var folders = await ViewModel().ListDraftConnectionFoldersAsync(new ConnectionFolderBrowseRequest(
            null, "sftp", StorageAuthorizationMode.UsernamePassword, "{\"Host\":\"h\"}", "/data",
            Username: " alice ", Password: "secret"));

        Assert.Equal("/data/child", Assert.Single(folders).Path);
        Assert.Equal("sftp", _browsed!.ProviderId);
        Assert.Equal("{\"Host\":\"h\"}", _browsed.SettingsJson);
        Assert.Equal("draft-payload", _browsed.EncryptedCredentialPayload);
        Assert.Equal("alice", _protected!["username"]);
        Assert.Equal("secret", _protected["password"]);
        Assert.Equal(_browsed.Id, _protectedContext!.ConnectionId);
        _connections.Verify(x => x.SaveAsync(It.IsAny<StorageConnection>(), default), Times.Never);
    }

    [Fact]
    public async Task NewConnection_ProtectsAnUnsavedPrivateKey()
    {
        await ViewModel().ListDraftConnectionFoldersAsync(new ConnectionFolderBrowseRequest(
            null, "sftp", StorageAuthorizationMode.SshKey, "{}", "/data", SshPrivateKey: [1, 2, 3]));

        Assert.Equal(Convert.ToBase64String([1, 2, 3]), _protected!["privateKeyBase64"]);
    }

    [Fact]
    public async Task RequiresManageConnections()
    {
        _management.Setup(x => x.HasGlobalPermissionAsync(It.IsAny<UserContext>(), ManagementPermission.ManageConnections))
            .ReturnsAsync(false);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            ViewModel().ListDraftConnectionFoldersAsync(new ConnectionFolderBrowseRequest(
                null, "sftp", StorageAuthorizationMode.SshKey, "{}", "/data")));

        VerifyNotBrowsed();
    }

    [Theory]
    [InlineData("smb", StorageAuthorizationMode.UsernamePassword)]
    [InlineData("sftp", StorageAuthorizationMode.DeviceCode)]
    public async Task RejectsProvidersWithoutPinnedSsh(string providerId, StorageAuthorizationMode mode)
    {
        await Assert.ThrowsAsync<NotSupportedException>(() =>
            ViewModel().ListDraftConnectionFoldersAsync(new ConnectionFolderBrowseRequest(
                null, providerId, mode, "{}", "/data")));

        VerifyNotBrowsed();
    }

    [Fact]
    public async Task ExistingConnection_NeverSendsTheStoredPasswordToAChangedHost()
    {
        await Assert.ThrowsAsync<ArgumentException>(() =>
            ViewModel().ListDraftConnectionFoldersAsync(new ConnectionFolderBrowseRequest(
                _stored.Id, "ignored", StorageAuthorizationMode.SshKey,
                "{\"Host\":\"evil\",\"RemoteRoot\":\"/\"}", "/data")));

        VerifyNotBrowsed();
    }

    [Fact]
    public async Task ExistingConnection_ReusesStoredCredentialsForAnotherFolderOnTheSameHost()
    {
        const string settings = "{\"Host\":\"old\",\"RemoteRoot\":\"/\"}";

        await ViewModel().ListDraftConnectionFoldersAsync(new ConnectionFolderBrowseRequest(
            _stored.Id, "ignored", StorageAuthorizationMode.SshKey, settings, "/data"));

        Assert.Equal(_stored.Id, _browsed!.Id);
        Assert.Equal("sftp", _browsed.ProviderId);
        Assert.Equal(StorageAuthorizationMode.UsernamePassword, _browsed.AuthorizationMode);
        Assert.Equal(settings, _browsed.SettingsJson);
        Assert.Equal("stored-payload", _browsed.EncryptedCredentialPayload);
        Assert.Equal(OldSettings, _stored.SettingsJson);
    }

    [Fact]
    public async Task ExistingConnection_TypedPasswordAllowsAChangedHost()
    {
        await ViewModel().ListDraftConnectionFoldersAsync(new ConnectionFolderBrowseRequest(
            _stored.Id, "ignored", StorageAuthorizationMode.SshKey, "{\"Host\":\"new\"}", "/data",
            Username: "alice", Password: "typed"));

        Assert.Equal("draft-payload", _browsed!.EncryptedCredentialPayload);
        Assert.Equal(_stored.Id, _protectedContext!.ConnectionId);
        Assert.Equal("stored-payload", _stored.EncryptedCredentialPayload);
    }

    [Theory]
    [InlineData("{\"Host\":\"a\",\"RemoteRoot\":\"/x\"}", "{\"Host\":\"a\",\"RemoteRoot\":\"/y\"}", true)]
    [InlineData("{\"Host\":\"a\",\"RemoteRoot\":\"/x\"}", "{\"Host\":\"b\",\"RemoteRoot\":\"/x\"}", false)]
    [InlineData("{\"Server\":\"a\"}", "{\"Server\":\"a\"}", true)]
    [InlineData(null, "{}", false)]
    [InlineData("not json", "not json", true)]
    public void SameTarget_IgnoresOnlyTheRemoteRoot(string? stored, string submitted, bool expected)
        => Assert.Equal(expected, CloudAccessViewModel.SameTarget(stored, submitted));

    private void VerifyNotBrowsed() => _targets.Verify(
        x => x.ListDirectoriesAsync(It.IsAny<StorageConnection>(), It.IsAny<string>(), default), Times.Never);

    private CloudAccessViewModel ViewModel()
    {
        var actor = new UserContext(new User(Guid.NewGuid(), "Alice", "alice", "hash", "nt"), [], [], []);
        var userContexts = new Mock<IUserContextFactory>();
        userContexts.Setup(x => x.CreateByUsernameAsync("alice")).ReturnsAsync(actor);
        var authentication = new Mock<AuthenticationStateProvider>();
        authentication.Setup(x => x.GetAuthenticationStateAsync()).ReturnsAsync(new AuthenticationState(
            new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.Name, "alice")], "test"))));
        var provider = new Mock<IStorageConnectionProvider>();
        provider.SetupGet(x => x.Id).Returns("sftp");
        provider.SetupGet(x => x.AuthorizationModes).Returns(new HashSet<StorageAuthorizationMode>
            { StorageAuthorizationMode.SshKey, StorageAuthorizationMode.UsernamePassword });
        var smb = new Mock<IStorageConnectionProvider>();
        smb.SetupGet(x => x.Id).Returns("smb");
        smb.SetupGet(x => x.AuthorizationModes).Returns(new HashSet<StorageAuthorizationMode>
            { StorageAuthorizationMode.UsernamePassword });
        return new CloudAccessViewModel(
            Mock.Of<ICloudAccessRepository>(), _connections.Object, Mock.Of<ICloudAuthorizationTicketStore>(),
            _management.Object, userContexts.Object, authentication.Object,
            Mock.Of<Kaimo_File_Server.Core.Repositories.Identity.IDepartmentRepository>(),
            Mock.Of<IUserRepository>(), Mock.Of<IGroupRepository>(), Mock.Of<IShareRepository>(),
            new OneDriveStorageConnectionFactory(
                _connections.Object, _vault.Object,
                Mock.Of<IStorageConnectionCredentialLeaseManager>(), Mock.Of<IHttpClientFactory>()),
            NullLogger<CloudAccessViewModel>.Instance,
            new StorageConnectionProviderCatalog([provider.Object, smb.Object]),
            _vault.Object,
            _targets.Object);
    }
}

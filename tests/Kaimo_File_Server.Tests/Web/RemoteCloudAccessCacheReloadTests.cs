using System.Security.Claims;
using Kaimo_File_Server.Core.Domain;
using Kaimo_File_Server.Core.Domain.Identity;
using Kaimo_File_Server.Core.Repositories;
using Kaimo_File_Server.Core.Security;
using Kaimo_File_Server.Core.Services;
using Kaimo_File_Server.Core.Services.ExternalStorage;
using Kaimo_File_Server.Web.Components.ViewModels;
using Kaimo_File_Server.Web.Services;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Kaimo_File_Server.Tests;

/// <summary>
/// The virtual share directory cache is shared across circuits. A full page reload starts
/// a new circuit (new scoped view model) and must not be served the cached listing of the
/// page it reloads; the context-menu refresh must clear the share's cache.
/// </summary>
public sealed class RemoteCloudAccessCacheReloadTests : IDisposable
{
    private readonly MemoryCache _memory = new(new MemoryCacheOptions());
    private readonly CloudAccessDirectoryCache _cache;
    private readonly CloudAccessShare _share;
    private readonly StorageConnection _connection = new()
    {
        ProviderId = "test", State = StorageConnectionState.Ready
    };
    private readonly Mock<IRemoteFileStore> _remote = new();
    private readonly Dictionary<string, int> _listCalls = new();

    public RemoteCloudAccessCacheReloadTests()
    {
        var settings = new Mock<ICloudAccessSettingsStore>();
        settings.Setup(x => x.GetAsync()).ReturnsAsync(CloudAccessRuntimeSettings.Default());
        _cache = new CloudAccessDirectoryCache(_memory, settings.Object);
        _share = new CloudAccessShare { ConnectionId = _connection.Id, Name = "Remote" };
        _remote.Setup(x => x.ListAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string path, CancellationToken _) =>
            {
                _listCalls[path] = _listCalls.GetValueOrDefault(path) + 1;
                return [];
            });
    }

    [Fact]
    public async Task InvalidatePath_ReloadsOnlyThatPath()
    {
        var shareId = Guid.NewGuid();
        var calls = new Dictionary<string, int>();
        Task Load(string path) => _cache.GetOrCreateAsync(shareId, path, () =>
        {
            calls[path] = calls.GetValueOrDefault(path) + 1;
            return Task.FromResult(new List<FileMetadata>());
        });

        await Load("Docs");
        await Load("Other");
        _cache.InvalidatePath(shareId, "docs/");
        await Load("Docs");
        await Load("Other");

        Assert.Equal(2, calls["Docs"]);
        Assert.Equal(1, calls["Other"]);
    }

    [Fact]
    public async Task FullPageReload_DoesNotServeCachedListingOfThatPage()
    {
        await NewViewModel().LoadShareAsync(_share.Id.ToString(), "Docs");
        // F5 on the same URL: new circuit, new scoped view model.
        await NewViewModel().LoadShareAsync(_share.Id.ToString(), "Docs");

        Assert.Equal(2, _listCalls["Docs"]);
    }

    [Fact]
    public async Task NavigationWithinCircuit_StillUsesCache()
    {
        var viewModel = NewViewModel();
        await viewModel.LoadShareAsync(_share.Id.ToString(), "Docs");
        await viewModel.LoadShareAsync(_share.Id.ToString(), "Other");
        await viewModel.LoadShareAsync(_share.Id.ToString(), "Docs");

        Assert.Equal(1, _listCalls["Docs"]);
    }

    [Fact]
    public async Task InvalidateListingCache_ClearsWholeShare()
    {
        var viewModel = NewViewModel();
        await viewModel.LoadShareAsync(_share.Id.ToString(), "Docs");
        await viewModel.LoadShareAsync(_share.Id.ToString(), "Other");

        ((IFileBrowserViewModel)viewModel).InvalidateListingCache();
        await viewModel.LoadShareAsync(_share.Id.ToString(), "Docs");
        await viewModel.LoadShareAsync(_share.Id.ToString(), "Other");

        Assert.Equal(2, _listCalls["Docs"]);
        Assert.Equal(2, _listCalls["Other"]);
    }

    private RemoteCloudAccessFileBrowserViewModel NewViewModel()
    {
        var actor = new UserContext(new User(Guid.NewGuid(), "Alice", "alice", "hash", "nt"), [], [], []);
        var repository = new Mock<ICloudAccessRepository>();
        repository.Setup(x => x.GetShareAsync(_share.Id, It.IsAny<CancellationToken>())).ReturnsAsync(_share);
        var connections = new Mock<IStorageConnectionRepository>();
        connections.Setup(x => x.GetAsync(_connection.Id, It.IsAny<CancellationToken>())).ReturnsAsync(_connection);
        var management = new Mock<IManagementAuthService>();
        management.Setup(x => x.GetAuthorizedDepartmentIdsAsync(actor, It.IsAny<ManagementPermission>()))
            .ReturnsAsync(AuthorizedScopeResult.Unrestricted());
        var userContexts = new Mock<IUserContextFactory>();
        userContexts.Setup(x => x.CreateByUsernameAsync("alice")).ReturnsAsync(actor);
        var authentication = new Mock<AuthenticationStateProvider>();
        authentication.Setup(x => x.GetAuthenticationStateAsync()).ReturnsAsync(new AuthenticationState(
            new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.Name, "alice")], "test"))));
        var session = new Mock<IStorageSession>();
        session.SetupGet(x => x.RemoteFiles).Returns(_remote.Object);
        var provider = new Mock<IStorageConnectionProvider>();
        provider.Setup(x => x.OpenSessionAsync(_connection, It.IsAny<CancellationToken>())).ReturnsAsync(session.Object);
        var providers = new Mock<IStorageConnectionProviderCatalog>();
        providers.Setup(x => x.GetRequired("test")).Returns(provider.Object);

        return new RemoteCloudAccessFileBrowserViewModel(
            repository.Object, connections.Object,
            new CloudAccessAuthorizationService(repository.Object, management.Object),
            userContexts.Object, authentication.Object, providers.Object,
            new CloudAccessDownloadTicketStore(), _cache,
            NullLogger<RemoteCloudAccessFileBrowserViewModel>.Instance);
    }

    public void Dispose() => _memory.Dispose();
}

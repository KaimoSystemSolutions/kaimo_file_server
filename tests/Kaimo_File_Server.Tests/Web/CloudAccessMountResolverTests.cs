using Kaimo_File_Server.Core.Domain;
using Kaimo_File_Server.Core.Domain.Identity;
using Kaimo_File_Server.Core.Repositories;
using Kaimo_File_Server.Core.Security;
using Kaimo_File_Server.Core.Services;
using Kaimo_File_Server.Web.Services;
using Microsoft.AspNetCore.Components.Authorization;
using Moq;
using Xunit;

namespace Kaimo_File_Server.Tests;

public sealed class CloudAccessMountResolverTests
{
    private readonly Mock<ICloudAccessRepository> _repository = new();
    private readonly Mock<IShareRepository> _shares = new();
    private readonly Mock<IAclService> _acl = new();
    private readonly UserContext _actor = new(new User(Guid.NewGuid(), "A", "alice", "h", "nt"), [], [], []);
    private readonly ShareDefinition _host = new("projekte", "/data/projekte", null, true, false, false);
    private readonly CloudAccessShare _visible;
    private readonly CloudAccessShare _hidden;

    public CloudAccessMountResolverTests()
    {
        _visible = Mount("Team/Cloud");
        _hidden = Mount("Team/Secret");
        var standalone = new CloudAccessShare { Name = "s" };
        _repository.Setup(r => r.GetSharesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync([_visible, _hidden, standalone]);
        _repository.Setup(r => r.GetEffectivePermissionAsync(It.IsAny<Guid>(), It.IsAny<IEnumerable<Guid>>()))
            .ReturnsAsync(CloudAccessPermission.Read);
        _shares.Setup(s => s.GetByIdAsync(_host.Id)).ReturnsAsync(_host);
        _acl.Setup(a => a.HasAccessAsync(_actor, _host.Id, It.IsAny<string>(), true, It.IsAny<FilePermission>()))
            .ReturnsAsync((UserContext _, Guid _, string p, bool _, FilePermission _) => p == "Team/Cloud");
    }

    private CloudAccessShare Mount(string path) => new()
    {
        Name = path, Provisioning = CloudAccessProvisioning.Folder, MountShareId = _host.Id, MountPath = path
    };

    private async Task<CloudAccessMountResolver> LoadedSut(bool canManage = false)
    {
        var management = new Mock<IManagementAuthService>();
        management.Setup(m => m.GetAuthorizedDepartmentIdsAsync(It.IsAny<UserContext>(), It.IsAny<ManagementPermission>()))
            .ReturnsAsync(canManage ? AuthorizedScopeResult.Unrestricted() : AuthorizedScopeResult.None());
        var authorization = new CloudAccessAuthorizationService(_repository.Object, management.Object, _shares.Object, _acl.Object);
        var sut = new CloudAccessMountResolver(_repository.Object, _shares.Object, authorization,
            new Mock<AuthenticationStateProvider>().Object, new Mock<IUserContextFactory>().Object);
        await sut.RefreshAsync(_host, _actor);
        return sut;
    }

    [Fact]
    public async Task HiddenMounts_AreNeverReturned()
    {
        var sut = await LoadedSut();
        Assert.Equal(["Cloud"], sut.ChildrenOf("projekte", "Team").Select(m => m.Name));
        Assert.Null(sut.Match("projekte", "Team/Secret"));
    }

    [Theory]
    [InlineData("Team/Cloud", "")]
    [InlineData("team/cloud/a/b.txt", "a/b.txt")]
    public async Task Match_ReturnsRemainingRemotePath(string route, string remote)
    {
        var match = (await LoadedSut()).Match("PROJEKTE", route);
        Assert.NotNull(match);
        Assert.Equal(_visible.Id, match.Mount.ShareId);
        Assert.Equal(remote, match.RemoteSubPath);
    }

    [Theory]
    [InlineData("Team")]
    [InlineData("Team/Cloud2")]
    [InlineData("")]
    public async Task Match_IgnoresPathsOutsideMounts(string route)
        => Assert.Null((await LoadedSut()).Match("projekte", route));

    [Fact]
    public async Task ChildrenOf_ListsDirectChildrenOnly()
    {
        var sut = await LoadedSut();
        Assert.Empty(sut.ChildrenOf("projekte", ""));
        Assert.Empty(sut.ChildrenOf("projekte", "Team/Cloud"));
    }

    [Fact]
    public async Task MountedFolder_BuildsRouteAndHostCrumbs()
    {
        var mount = (await LoadedSut()).Match("projekte", "Team/Cloud")!.Mount;
        Assert.Equal("/files/projekte/Team/Cloud", mount.RouteBase);
        Assert.Equal(
            [("projekte", "/files/projekte"), ("Team", "/files/projekte/Team")],
            mount.ParentCrumbs);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MountedFolder_CarriesTheVirtualShareManagementRight(bool canManage)
    {
        var mount = (await LoadedSut(canManage)).Match("projekte", "Team/Cloud")!.Mount;
        Assert.Equal(canManage, mount.CanManage);
    }

    [Theory]
    [InlineData("", "projekte/Team/Cloud")]
    [InlineData("a/b", "projekte/Team/Cloud/a/b")]
    public async Task MountedFolder_HostLocationOf_PrefixesHostShareAndMountPath(string remote, string expected)
    {
        var mount = (await LoadedSut()).Match("projekte", "Team/Cloud")!.Mount;
        Assert.Equal(expected, mount.HostLocationOf(remote));
    }

    [Fact]
    public async Task DisabledOrHomesHost_HasNoMounts()
    {
        _host.IsUserHomes = true;
        var sut = await LoadedSut();
        Assert.Null(sut.Match("projekte", "Team/Cloud"));
        Assert.True(sut.HasLoaded("projekte"));
    }
}

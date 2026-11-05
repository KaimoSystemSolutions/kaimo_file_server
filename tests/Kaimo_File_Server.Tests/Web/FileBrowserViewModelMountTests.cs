using System.Security.Claims;
using Kaimo_File_Server.Core.Domain;
using Kaimo_File_Server.Core.Domain.Identity;
using Kaimo_File_Server.Core.Repositories;
using Kaimo_File_Server.Core.Security;
using Kaimo_File_Server.Core.Services;
using Kaimo_File_Server.Core.Services.DataServices;
using Kaimo_File_Server.Core.Services.File;
using Kaimo_File_Server.Infrastructure.Persistence;
using Kaimo_File_Server.Search;
using Kaimo_File_Server.Web.Components.ViewModels;
using Kaimo_File_Server.Web.Services;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Kaimo_File_Server.Tests;

/// <summary>Mounted virtual shares appear as open-only folder entries in the host listing.</summary>
public sealed class FileBrowserViewModelMountTests
{
    private readonly ShareDefinition _host = new("projekte", "/data/projekte", isEnabled: true);
    private readonly UserContext _actor = new(new User(Guid.NewGuid(), "Alice", "alice", "hash", "nt"), [], [], []);
    private readonly Mock<IFileService> _files = new();
    private readonly Mock<IAclService> _acl = new();
    private readonly Mock<IShareRepository> _shareRepo = new();
    private readonly Mock<ICloudAccessRepository> _cloud = new();
    private readonly CloudAccessShare _mount;

    public FileBrowserViewModelMountTests()
    {
        _shareRepo.Setup(r => r.GetByNameAsync("projekte")).ReturnsAsync(_host);
        _shareRepo.Setup(r => r.GetByIdAsync(_host.Id)).ReturnsAsync(_host);
        _files.Setup(s => s.ListAsync("Team", It.IsAny<UserContext>())).ReturnsAsync(
        [
            new FileMetadata { Path = "/data/projekte/Team/Doc", Name = "Doc", IsDirectory = true },
            // A real folder that won a creation race is shadowed by the mount.
            new FileMetadata { Path = "/data/projekte/Team/cloud", Name = "cloud", IsDirectory = true },
        ]);
        _mount = new CloudAccessShare
        {
            Name = "remote", Provisioning = CloudAccessProvisioning.Folder,
            MountShareId = _host.Id, MountPath = "Team/Cloud"
        };
        _cloud.Setup(r => r.GetSharesAsync(It.IsAny<CancellationToken>())).ReturnsAsync([_mount]);
        _cloud.Setup(r => r.GetEffectivePermissionAsync(_mount.Id, It.IsAny<IEnumerable<Guid>>()))
            .ReturnsAsync(CloudAccessPermission.Read);
        HostRead(true);
    }

    private void HostRead(bool allowed) => _acl
        .Setup(a => a.HasAccessAsync(It.IsAny<UserContext>(), _host.Id, "Team/Cloud", true, It.IsAny<FilePermission>()))
        .ReturnsAsync(allowed);

    private FileBrowserViewModel Sut(bool withResolver = true)
    {
        var factory = new Mock<IFileServiceFactory>();
        factory.Setup(f => f.CreateForShare(It.IsAny<ShareDefinition>())).Returns(_files.Object);
        var authState = new Mock<AuthenticationStateProvider>();
        authState.Setup(a => a.GetAuthenticationStateAsync()).ReturnsAsync(new AuthenticationState(
            new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.Name, "alice")], "test"))));
        var users = new Mock<IUserContextFactory>();
        users.Setup(f => f.CreateByUsernameAsync("alice")).ReturnsAsync(_actor);
        var management = new Mock<IManagementAuthService>();
        management.Setup(m => m.GetAuthorizedDepartmentIdsAsync(It.IsAny<UserContext>(), It.IsAny<ManagementPermission>()))
            .ReturnsAsync(AuthorizedScopeResult.None());
        var links = new Mock<IShareLinkRepository>();
        links.Setup(r => r.ListForSharesAsync(It.IsAny<IEnumerable<Guid>>())).ReturnsAsync(new List<ShareLink>());
        var syncs = new Mock<ISyncDefinitionRepository>();
        syncs.Setup(r => r.GetAllAsync(It.IsAny<CancellationToken>())).ReturnsAsync(new List<SyncDefinitionAdminEntry>());

        var resolver = new CloudAccessMountResolver(_cloud.Object, _shareRepo.Object,
            new CloudAccessAuthorizationService(_cloud.Object, management.Object, _shareRepo.Object, _acl.Object),
            authState.Object, users.Object);

        return new FileBrowserViewModel(
            factory.Object, _shareRepo.Object, new Mock<IDbContextFactory<ApplicationDbContext>>().Object,
            users.Object, management.Object, authState.Object,
            NullLogger<FileBrowserViewModel>.Instance, new Mock<ISearchService>().Object,
            new Mock<IUserRepository>().Object, new FileDownloadTicketStore(), new ZipDownloadTicketStore(),
            new DemoModeOptions(), syncs.Object, links.Object,
            mountResolver: withResolver ? resolver : null);
    }

    [Fact]
    public async Task VisibleMount_IsInjectedAsOpenOnlyFolder_AndShadowsRaceFolder()
    {
        var sut = Sut();
        await sut.LoadShareAsync("projekte", "Team");

        Assert.Null(sut.ErrorMessage);
        Assert.Equal(["Doc", "Cloud"], sut.Items.Select(i => i.Name));
        var entry = sut.Items.Single(i => i.Name == "Cloud");
        Assert.True(entry.IsDirectory);
        Assert.True(sut.IsMountEntry(entry));
        Assert.Equal("Team/Cloud", sut.ShareRelativeOf(entry));
        Assert.False(sut.IsMountEntry(sut.Items[0]));
        Assert.Equal("Team/Cloud", sut.MountOf(entry)?.MountPath);
        Assert.Null(sut.MountOf(sut.Items[0]));
    }

    [Fact]
    public async Task HiddenMount_IsNotInjected()
    {
        HostRead(false);
        var sut = Sut();
        await sut.LoadShareAsync("projekte", "Team");

        Assert.Equal(["Doc", "cloud"], sut.Items.Select(i => i.Name));
    }

    [Fact]
    public async Task BrowserWithoutResolver_NeverShowsMounts()
    {
        var sut = Sut(withResolver: false);
        await sut.LoadShareAsync("projekte", "Team");

        Assert.DoesNotContain(sut.Items, i => sut.IsMountEntry(i));
    }

    [Fact]
    public async Task SelectionDownload_SkipsMountEntries()
    {
        var sut = Sut();
        await sut.LoadShareAsync("projekte", "Team");

        Assert.Null(await sut.GetSelectionDownloadUrlAsync([sut.Items.Single(i => i.Name == "Cloud")]));
    }

    [Fact]
    public async Task ReservedName_MapsToNameReservedError()
    {
        _files.Setup(s => s.CreateDirectoryAsync(It.IsAny<string>(), It.IsAny<UserContext>()))
            .ThrowsAsync(new MountPathReservedException("Team/Cloud"));
        var sut = Sut();
        await sut.LoadShareAsync("projekte", "Team");

        var result = await sut.CreateFolderAsync("Cloud");

        Assert.False(result.Success);
        Assert.NotEqual(Kaimo_File_Server.Core.Language.Resources.Web_Error_AccessDenied, result.Error);
    }
}

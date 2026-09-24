using Kaimo_File_Server.Core.Domain;
using Kaimo_File_Server.Core.Domain.Identity;
using Kaimo_File_Server.Core.Repositories;
using Kaimo_File_Server.Core.Security;
using Kaimo_File_Server.Core.Services;
using Kaimo_File_Server.Core.Services.File;
using Kaimo_File_Server.Infrastructure.Configuration;
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

/// <summary>
/// Pins that an anonymous public share link never reveals internal information: neither the
/// sync configuration of the shared folder nor the real (internal) share name.
/// </summary>
public class PublicShareFileBrowserViewModelTests
{
    private readonly Mock<ISyncDefinitionRepository> _syncRepo = new();
    private readonly ShareDefinition _share = new("internal-share", "/data/internal-share", isEnabled: true);
    private readonly PublicShareFileBrowserViewModel _sut;

    public PublicShareFileBrowserViewModelTests()
    {
        var creator = new User(Guid.NewGuid(), "Alice", "alice", "hash", "nt");

        var shareRepo = new Mock<IShareRepository>();
        shareRepo.Setup(r => r.GetByNameAsync(_share.Name)).ReturnsAsync(_share);

        var fileService = new Mock<IFileService>();
        fileService
            .Setup(s => s.ListAsync(It.IsAny<string>(), It.IsAny<UserContext>()))
            .ReturnsAsync(new List<FileMetadata>());
        var fileServiceFactory = new Mock<IFileServiceFactory>();
        fileServiceFactory.Setup(f => f.CreateForShare(_share.Id, _share.Path)).Returns(fileService.Object);

        var userContexts = new Mock<IUserContextFactory>();
        userContexts
            .Setup(f => f.CreateByUserIdAsync(creator.Id))
            .ReturnsAsync(new UserContext(creator, [], [], []));

        var mgmtAuth = new Mock<IManagementAuthService>();
        mgmtAuth
            .Setup(m => m.HasAnyPermissionAsync(It.IsAny<UserContext>(), It.IsAny<ManagementPermission>()))
            .ReturnsAsync(true);

        var shareLinks = new ShareLinkService(
            Mock.Of<IShareLinkRepository>(), Mock.Of<IConfigRepository>(), Mock.Of<IPasswordService>(),
            Mock.Of<ILoginThrottle>(), mgmtAuth.Object, userContexts.Object,
            Mock.Of<AuthenticationStateProvider>(), new PublicDownloadTicketStore());

        _sut = new PublicShareFileBrowserViewModel(
            fileServiceFactory.Object, shareRepo.Object, Mock.Of<IDbContextFactory<ApplicationDbContext>>(),
            userContexts.Object, mgmtAuth.Object, Mock.Of<AuthenticationStateProvider>(),
            NullLogger<FileBrowserViewModel>.Instance, Mock.Of<ISearchService>(), Mock.Of<IUserRepository>(),
            new FileDownloadTicketStore(), new ZipDownloadTicketStore(), new DemoModeOptions(), _syncRepo.Object,
            Mock.Of<IShareLinkRepository>(), shareLinks);

        _sut.Initialize(new ShareLink
        {
            ShareId = _share.Id,
            RootRelativePath = "projects/secret",
            IsDirectory = true,
            DisplayName = "Public folder",
            CreatedByUserId = creator.Id,
        });
    }

    [Fact]
    public async Task LoadShareAsync_NeverLoadsSyncState()
    {
        await _sut.LoadShareAsync(_share.Name);

        Assert.Null(_sut.ErrorMessage);
        Assert.False(_sut.CanManageSyncs);
        Assert.Null(_sut.GetSyncMarker(new FileMetadata { Path = "/data/internal-share/projects/secret/a" }));
        _syncRepo.Verify(r => r.GetAllAsync(It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task CurrentBrowserShare_ExposesOnlyTheLinkDisplayName()
    {
        await _sut.LoadShareAsync(_share.Name);

        Assert.Equal("Public folder", _sut.CurrentBrowserShare?.Name);
    }
}

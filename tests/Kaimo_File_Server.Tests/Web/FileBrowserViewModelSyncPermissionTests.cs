using System.Security.Claims;
using Kaimo_File_Server.Core.Domain;
using Kaimo_File_Server.Core.Domain.Identity;
using Kaimo_File_Server.Core.Repositories;
using Kaimo_File_Server.Core.Security;
using Kaimo_File_Server.Core.Services;
using Kaimo_File_Server.Core.Services.DataServices;
using Kaimo_File_Server.Core.Services.File;
using Kaimo_File_Server.Infrastructure.Clouds;
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
/// Phase 4 of the scoped-permission audit (F18): the file browser offers the sync controls only
/// for a share whose syncs the actor may manage — the same share set the sync overview lists —
/// not for every share as soon as the actor manages syncs anywhere.
/// </summary>
public class FileBrowserViewModelSyncPermissionTests
{
    private readonly ShareDefinition _share = new("share", "/data/share", isEnabled: true);
    private readonly Mock<IManagementAuthService> _mgmtAuth = new();
    private readonly FileBrowserViewModel _sut;

    public FileBrowserViewModelSyncPermissionTests()
    {
        var shareRepo = new Mock<IShareRepository>();
        shareRepo.Setup(r => r.GetByNameAsync("share")).ReturnsAsync(_share);

        var fileService = new Mock<IFileService>();
        fileService
            .Setup(s => s.ListAsync(It.IsAny<string>(), It.IsAny<UserContext>()))
            .ReturnsAsync(new List<FileMetadata>());
        var fileServiceFactory = new Mock<IFileServiceFactory>();
        fileServiceFactory.Setup(f => f.CreateForShare(It.IsAny<ShareDefinition>())).Returns(fileService.Object);

        var authState = new Mock<AuthenticationStateProvider>();
        authState
            .Setup(a => a.GetAuthenticationStateAsync())
            .ReturnsAsync(new AuthenticationState(new ClaimsPrincipal(
                new ClaimsIdentity([new Claim(ClaimTypes.Name, "alice")], authenticationType: "test"))));
        var userContextFactory = new Mock<IUserContextFactory>();
        userContextFactory
            .Setup(f => f.CreateByUsernameAsync("alice"))
            .ReturnsAsync(new UserContext(new User(Guid.NewGuid(), "Alice", "alice", "hash", "nt"), [], [], []));

        var shareLinkRepo = new Mock<IShareLinkRepository>();
        shareLinkRepo
            .Setup(r => r.ListForSharesAsync(It.IsAny<IEnumerable<Guid>>()))
            .ReturnsAsync(new List<ShareLink>());
        var syncRepo = new Mock<ISyncDefinitionRepository>();
        syncRepo.Setup(r => r.GetAllAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<SyncDefinitionAdminEntry>());
        var runner = new Mock<ICloudSyncJobRunner>();
        runner.Setup(r => r.Jobs).Returns([]);

        // The actor manages syncs somewhere.
        _mgmtAuth.Setup(m => m.HasAnyPermissionAsync(It.IsAny<UserContext>(), It.IsAny<ManagementPermission>()))
            .ReturnsAsync(true);

        _sut = new FileBrowserViewModel(
            fileServiceFactory.Object, shareRepo.Object, new Mock<IDbContextFactory<ApplicationDbContext>>().Object,
            userContextFactory.Object, _mgmtAuth.Object, authState.Object,
            NullLogger<FileBrowserViewModel>.Instance, new Mock<ISearchService>().Object,
            new Mock<IUserRepository>().Object, new FileDownloadTicketStore(), new ZipDownloadTicketStore(),
            new DemoModeOptions(), syncRepo.Object, shareLinkRepo.Object, runner.Object);
    }

    private void SyncScope(AuthorizedScopeResult scope)
        => _mgmtAuth.Setup(m => m.GetAuthorizedShareIdsAnyAsync(It.IsAny<UserContext>(), ManagementPermission.SyncAdmin))
            .ReturnsAsync(scope);

    [Fact]
    public async Task SyncRightsOnAnotherShareOnly_HideTheSyncControls()
    {
        SyncScope(AuthorizedScopeResult.LimitedTo([Guid.NewGuid()]));
        await _sut.LoadShareAsync("share");
        Assert.False(_sut.CanManageSyncs);
    }

    [Fact]
    public async Task SyncRightsOnThisShare_ShowTheSyncControls()
    {
        SyncScope(AuthorizedScopeResult.LimitedTo([_share.Id]));
        await _sut.LoadShareAsync("share");
        Assert.True(_sut.CanManageSyncs);
    }

    [Fact]
    public async Task GlobalSyncRights_ShowTheSyncControls()
    {
        SyncScope(AuthorizedScopeResult.Unrestricted());
        await _sut.LoadShareAsync("share");
        Assert.True(_sut.CanManageSyncs);
    }
}

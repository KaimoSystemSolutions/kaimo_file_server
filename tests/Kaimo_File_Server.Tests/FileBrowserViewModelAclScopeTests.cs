using System.Security.Claims;
using Kaimo_File_Server.Core.Domain;
using Kaimo_File_Server.Core.Domain.Identity;
using Kaimo_File_Server.Core.Repositories;
using Kaimo_File_Server.Core.Security;
using Kaimo_File_Server.Core.Services;
using Kaimo_File_Server.Core.Services.File;
using Kaimo_File_Server.Infrastructure.Persistence;
using Kaimo_File_Server.Search;
using Kaimo_File_Server.Web.Components.ViewModels;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Kaimo_File_Server.Tests;

/// <summary>
/// Pins the file browser's ACL-management gate to a SCOPED permission decision. Previously the
/// view (via the VM) trusted a coarse global role name ("Administrator"/"ShareManager"); now the
/// VM resolves <see cref="ManagementPermission.ManageShareAcls"/> for the specific share, so a
/// department-/share-scoped manager only gets the ACL UI within their scope. Also verifies that
/// ACL counts are never queried from the database for users who cannot manage ACLs.
/// </summary>
public class FileBrowserViewModelAclScopeTests
{
    private readonly Mock<IFileServiceFactory> _fileServiceFactory = new();
    private readonly Mock<IFileService> _fileService = new();
    private readonly Mock<IShareRepository> _shareRepo = new();
    private readonly Mock<IDbContextFactory<ApplicationDbContext>> _dbFactory = new();
    private readonly Mock<IUserContextFactory> _userContextFactory = new();
    private readonly Mock<IManagementAuthService> _mgmtAuth = new();
    private readonly Mock<AuthenticationStateProvider> _authState = new();
    private readonly Mock<ISearchService> _searchService = new();
    private readonly Mock<IUserRepository> _userRepo = new();

    private readonly ShareDefinition _share = new("share", "/data/share", isEnabled: true);
    private readonly FileBrowserViewModel _sut;

    public FileBrowserViewModelAclScopeTests()
    {
        _shareRepo.Setup(r => r.GetByNameAsync("share")).ReturnsAsync(_share);

        _fileServiceFactory
            .Setup(f => f.CreateForShare(_share.Id, _share.Path))
            .Returns(_fileService.Object);
        _fileService
            .Setup(s => s.ListAsync(It.IsAny<string>(), It.IsAny<UserContext>()))
            .ReturnsAsync(new List<FileMetadata>());

        var identity = new ClaimsIdentity(
            new[] { new Claim(ClaimTypes.Name, "alice") }, authenticationType: "test");
        _authState
            .Setup(a => a.GetAuthenticationStateAsync())
            .ReturnsAsync(new AuthenticationState(new ClaimsPrincipal(identity)));

        var user = new User(Guid.NewGuid(), "Alice", "alice", "hash", "nt");
        _userContextFactory
            .Setup(f => f.CreateByUsernameAsync("alice"))
            .ReturnsAsync(new UserContext(user, [], [], []));

        _sut = new FileBrowserViewModel(
            _fileServiceFactory.Object, _shareRepo.Object, _dbFactory.Object,
            _userContextFactory.Object, _mgmtAuth.Object, _authState.Object,
            NullLogger<FileBrowserViewModel>.Instance, _searchService.Object, _userRepo.Object);
    }

    private void Authorize(bool allowed) =>
        _mgmtAuth
            .Setup(m => m.CanManageShareAsync(
                It.IsAny<UserContext>(), _share.Id, ManagementPermission.ManageShareAcls))
            .ReturnsAsync(allowed);

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task LoadShareAsync_SetsCanManageAcls_FromScopedShareCheck(bool allowed)
    {
        Authorize(allowed);

        await _sut.LoadShareAsync("share");

        Assert.Equal(allowed, _sut.CanManageAcls);
        // The decision must be the scoped ManageShareAcls check for THIS share.
        _mgmtAuth.Verify(
            m => m.CanManageShareAsync(
                It.IsAny<UserContext>(), _share.Id, ManagementPermission.ManageShareAcls),
            Times.Once);
    }

    [Fact]
    public async Task LoadAclCountsAsync_WhenCannotManage_SkipsDatabase()
    {
        Authorize(false);
        await _sut.LoadShareAsync("share");

        await _sut.LoadAclCountsAsync();

        Assert.Empty(_sut.AclCounts);
        // No ACL-count query may run for a user who cannot manage this share's ACLs.
        _dbFactory.Verify(
            f => f.CreateDbContextAsync(It.IsAny<CancellationToken>()), Times.Never);
    }
}

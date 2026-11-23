using System.Security.Claims;
using Kaimo_File_Server.Core.Domain;
using Kaimo_File_Server.Core.Domain.Identity;
using Kaimo_File_Server.Core.Language;
using Kaimo_File_Server.Core.Repositories.ExternalStorage;
using Kaimo_File_Server.Core.Repositories.Identity;
using Kaimo_File_Server.Core.Repositories.Shares;
using Kaimo_File_Server.Core.Security.Demo;
using Kaimo_File_Server.Core.Services.File;
using Kaimo_File_Server.Core.Services.Management;
using Kaimo_File_Server.Core.Services.Search;
using Kaimo_File_Server.Infrastructure.Persistence;
using Kaimo_File_Server.Web.Components.ViewModels;
using Kaimo_File_Server.Web.Services.Downloads;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Kaimo_File_Server.Web.Tests.ViewModels.Files;

/// <summary>
/// A path that names a file (pasted into the path bar or opened as URL) opens the file's
/// folder and asks the view to select the file, like Explorer does.
/// </summary>
public class FileBrowserViewModelFilePathTests
{
    private readonly Mock<IFileService> _files = new();
    private readonly FileBrowserViewModel _sut;

    private static readonly FileMetadata Report = new() { Name = "report.txt", Path = "docs/report.txt" };

    public FileBrowserViewModelFilePathTests()
    {
        var factory = new Mock<IFileServiceFactory>();
        factory.Setup(f => f.CreateForShare(It.IsAny<ShareDefinition>())).Returns(_files.Object);
        var shares = new Mock<IShareRepository>();
        shares.Setup(r => r.GetByNameAsync("projects"))
            .ReturnsAsync(new ShareDefinition("projects", "/data/projects", isEnabled: true));

        var identity = new ClaimsIdentity(new[] { new Claim(ClaimTypes.Name, "bob") }, authenticationType: "test");
        var authState = new Mock<AuthenticationStateProvider>();
        authState.Setup(a => a.GetAuthenticationStateAsync())
            .ReturnsAsync(new AuthenticationState(new ClaimsPrincipal(identity)));
        var contexts = new Mock<IUserContextFactory>();
        contexts.Setup(f => f.CreateByUsernameAsync("bob"))
            .ReturnsAsync(new UserContext(new User(Guid.NewGuid(), "Bob", "bob", "hash", "nt"), [], [], []));
        var syncs = new Mock<ISyncDefinitionRepository>();
        syncs.Setup(r => r.GetAllAsync(It.IsAny<CancellationToken>())).ReturnsAsync(new List<SyncDefinitionAdminEntry>());
        var links = new Mock<IShareLinkRepository>();
        links.Setup(r => r.ListForSharesAsync(It.IsAny<IEnumerable<Guid>>())).ReturnsAsync(new List<ShareLink>());

        _files.Setup(s => s.ListAsync("docs", It.IsAny<UserContext>())).ReturnsAsync([Report]);
        _files.Setup(s => s.ListAsync("docs/report.txt", It.IsAny<UserContext>()))
            .ThrowsAsync(new DirectoryNotFoundException());
        _files.Setup(s => s.FindEntryAsync("docs/report.txt", It.IsAny<UserContext>())).ReturnsAsync(Report);

        _sut = new FileBrowserViewModel(
            factory.Object, shares.Object, Mock.Of<IDbContextFactory<ApplicationDbContext>>(),
            contexts.Object, Mock.Of<IManagementAuthService>(), authState.Object,
            NullLogger<FileBrowserViewModel>.Instance, Mock.Of<ISearchService>(), Mock.Of<IUserRepository>(),
            new FileDownloadTicketStore(), new ZipDownloadTicketStore(), new DemoModeOptions(), syncs.Object,
            links.Object);
    }

    [Fact]
    public async Task A_file_path_opens_its_folder_and_names_the_file_to_select()
    {
        await _sut.LoadShareAsync("projects", "docs/report.txt");

        Assert.Null(_sut.ErrorMessage);
        Assert.Equal("docs", _sut.CurrentPath);
        Assert.Equal("report.txt", _sut.FileToSelect);
        Assert.Same(Report, Assert.Single(_sut.Items));
    }

    [Fact]
    public async Task A_folder_path_selects_nothing()
    {
        await _sut.LoadShareAsync("projects", "docs/report.txt");
        await _sut.LoadShareAsync("projects", "docs");

        Assert.Equal("docs", _sut.CurrentPath);
        Assert.Null(_sut.FileToSelect);
    }

    [Fact]
    public async Task A_missing_path_stays_not_found()
    {
        _files.Setup(s => s.ListAsync("docs/gone.txt", It.IsAny<UserContext>()))
            .ThrowsAsync(new DirectoryNotFoundException());

        await _sut.LoadShareAsync("projects", "docs/gone.txt");

        Assert.Equal(Resources.Web_Error_FileOrFolderNotFound, _sut.ErrorMessage);
        Assert.Null(_sut.FileToSelect);
        Assert.Empty(_sut.Items);
    }

    [Fact]
    public async Task A_denied_path_that_is_no_visible_file_stays_denied()
    {
        _files.Setup(s => s.ListAsync("secret/x.txt", It.IsAny<UserContext>()))
            .ThrowsAsync(new UnauthorizedAccessException());
        _files.Setup(s => s.FindEntryAsync("secret/x.txt", It.IsAny<UserContext>()))
            .ThrowsAsync(new UnauthorizedAccessException());

        await _sut.LoadShareAsync("projects", "secret/x.txt");

        Assert.Equal(Resources.Web_Error_AccessDenied, _sut.ErrorMessage);
        Assert.Null(_sut.FileToSelect);
    }
}

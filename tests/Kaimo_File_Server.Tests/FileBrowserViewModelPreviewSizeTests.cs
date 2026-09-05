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
using Kaimo_File_Server.Web.Services;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Kaimo_File_Server.Tests;

/// <summary>
/// Covers the file browser's inline-preview memory cap and the directory-size
/// background calculation: oversized files must never be buffered into memory, and
/// sizes for every sub-directory must land in the (concurrent) size map.
/// </summary>
public class FileBrowserViewModelPreviewSizeTests
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
    private readonly Mock<ISyncDefinitionRepository> _syncRepo = new();

    private readonly ShareDefinition _share = new("share", "/data/share", isEnabled: true);
    private readonly FileBrowserViewModel _sut;

    private List<FileMetadata> _listing = new();

    public FileBrowserViewModelPreviewSizeTests()
    {
        _shareRepo.Setup(r => r.GetByNameAsync("share")).ReturnsAsync(_share);
        _fileServiceFactory
            .Setup(f => f.CreateForShare(_share.Id, _share.Path))
            .Returns(_fileService.Object);
        _fileService
            .Setup(s => s.ListAsync(It.IsAny<string>(), It.IsAny<UserContext>()))
            .ReturnsAsync(() => _listing);

        var identity = new ClaimsIdentity(
            new[] { new Claim(ClaimTypes.Name, "alice") }, authenticationType: "test");
        _authState
            .Setup(a => a.GetAuthenticationStateAsync())
            .ReturnsAsync(new AuthenticationState(new ClaimsPrincipal(identity)));
        var user = new User(Guid.NewGuid(), "Alice", "alice", "hash", "nt");
        _userContextFactory
            .Setup(f => f.CreateByUsernameAsync("alice"))
            .ReturnsAsync(new UserContext(user, [], [], []));

        _syncRepo
            .Setup(r => r.GetAllAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<SyncDefinitionAdminEntry>());

        _sut = new FileBrowserViewModel(
            _fileServiceFactory.Object, _shareRepo.Object, _dbFactory.Object,
            _userContextFactory.Object, _mgmtAuth.Object, _authState.Object,
            NullLogger<FileBrowserViewModel>.Instance, _searchService.Object, _userRepo.Object,
            new FileDownloadTicketStore(), new DemoModeOptions(), _syncRepo.Object);
    }

    private static FileMetadata Dir(string name) =>
        new() { Name = name, Path = name, IsDirectory = true, Acl = [] };

    private static FileMetadata File(string name, long size) =>
        new() { Name = name, Path = name, IsDirectory = false, Size = size, Acl = [] };

    // ═══════════════════ Preview memory cap (#7) ═══════════════════

    [Fact]
    public async Task ReadFileForPreviewAsync_WhenFileExceedsCap_ReturnsNullWithoutReading()
    {
        await _sut.LoadShareAsync("share");
        var big = File("huge.bin", _sut.GetMaxPreviewSizeBytes() + 1);

        var result = await _sut.ReadFileForPreviewAsync(big);

        Assert.Null(result);
        // The whole point: an oversized file is never streamed into a server byte[].
        _fileService.Verify(
            s => s.ReadFileAsync(It.IsAny<string>(), It.IsAny<UserContext>()), Times.Never);
    }

    [Fact]
    public async Task ReadFileForPreviewAsync_WhenWithinCap_ReadsFile()
    {
        await _sut.LoadShareAsync("share");
        var small = File("note.txt", 1024);
        _fileService
            .Setup(s => s.ReadFileAsync("note.txt", It.IsAny<UserContext>()))
            .ReturnsAsync(() => new MemoryStream(new byte[] { 1, 2, 3 }));

        var result = await _sut.ReadFileForPreviewAsync(small);

        Assert.NotNull(result);
        Assert.Equal(new byte[] { 1, 2, 3 }, result!.Value.Data);
        _fileService.Verify(s => s.ReadFileAsync("note.txt", It.IsAny<UserContext>()), Times.Once);
    }

    // ═══════════════════ Directory sizes (#5 / #6) ═══════════════════

    [Fact]
    public async Task LoadDirectorySizesInBackgroundAsync_ComputesSizeForEveryDirectory()
    {
        var a = Dir("a");
        var b = Dir("b");
        var c = Dir("c");
        _listing = new List<FileMetadata> { a, b, c };
        _fileService
            .Setup(s => s.GetDirectorySizeAsync(It.IsAny<string>(), It.IsAny<UserContext>()))
            .ReturnsAsync((string path, UserContext _) => path switch
            {
                "a" => 100L,
                "b" => 200L,
                "c" => 300L,
                _ => 0L
            });

        await _sut.LoadShareAsync("share");
        // Awaitable direct call supersedes the fire-and-forget run started by LoadShareAsync.
        await _sut.LoadDirectorySizesInBackgroundAsync();

        Assert.Equal(100L, _sut.GetDirectorySize(a));
        Assert.Equal(200L, _sut.GetDirectorySize(b));
        Assert.Equal(300L, _sut.GetDirectorySize(c));
    }
}

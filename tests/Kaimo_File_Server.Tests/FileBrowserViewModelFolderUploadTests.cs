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
using Kaimo_File_Server.Web.Helpers;
using Kaimo_File_Server.Web.Services;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Kaimo_File_Server.Tests;

/// <summary>
/// Web folder uploads: the client-supplied folder of each file is validated
/// (<see cref="UploadFolderPath"/>), missing folders are created below the current
/// directory without touching existing ones, and files land in — and conflict within —
/// their sub-folder.
/// </summary>
public sealed class FileBrowserViewModelFolderUploadTests : IDisposable
{
    // Folder existence is probed on disk, so share paths map into this temp folder.
    private readonly string _root = Directory.CreateTempSubdirectory("kaimo-folder-upload-").FullName;

    private readonly Mock<IFileServiceFactory> _fileServiceFactory = new();
    private readonly Mock<IFileService> _fileService = new();
    private readonly Mock<IShareRepository> _shareRepo = new();
    private readonly Mock<IUserContextFactory> _userContextFactory = new();
    private readonly Mock<AuthenticationStateProvider> _authState = new();
    private readonly Mock<ISyncDefinitionRepository> _syncRepo = new();

    private readonly ShareDefinition _share = new("share", "/data/share", isEnabled: true);
    private readonly FileBrowserViewModel _sut;

    public FileBrowserViewModelFolderUploadTests()
    {
        _shareRepo.Setup(r => r.GetByNameAsync("share")).ReturnsAsync(_share);
        _fileServiceFactory
            .Setup(f => f.CreateForShare(It.Is<ShareDefinition>(s => s.Id == _share.Id)))
            .Returns(_fileService.Object);
        _fileService
            .Setup(s => s.ListAsync(It.IsAny<string>(), It.IsAny<UserContext>()))
            .ReturnsAsync([]);
        // Nothing exists unless a test says otherwise.
        _fileService
            .Setup(s => s.ReadFileAsync(It.IsAny<string>(), It.IsAny<UserContext>()))
            .ThrowsAsync(new FileNotFoundException());
        // Like the real FileService: a missing path is reported as a file, never as missing.
        _fileService
            .Setup(s => s.GetMetadataAsync(It.IsAny<string>(), It.IsAny<UserContext>()))
            .ReturnsAsync(new FileMetadata { IsDirectory = false, Acl = [] });
        _fileService
            .Setup(s => s.ToAbsolutePath(It.IsAny<string>()))
            .Returns<string>(Abs);

        var identity = new ClaimsIdentity(
            new[] { new Claim(ClaimTypes.Name, "alice") }, authenticationType: "test");
        _authState
            .Setup(a => a.GetAuthenticationStateAsync())
            .ReturnsAsync(new AuthenticationState(new ClaimsPrincipal(identity)));
        _userContextFactory
            .Setup(f => f.CreateByUsernameAsync("alice"))
            .ReturnsAsync(new UserContext(new User(Guid.NewGuid(), "Alice", "alice", "hash", "nt"), [], [], []));

        _syncRepo
            .Setup(r => r.GetAllAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<SyncDefinitionAdminEntry>());
        var shareLinkRepo = new Mock<IShareLinkRepository>();
        shareLinkRepo
            .Setup(r => r.ListForSharesAsync(It.IsAny<IEnumerable<Guid>>()))
            .ReturnsAsync(new List<ShareLink>());

        _sut = new FileBrowserViewModel(
            _fileServiceFactory.Object, _shareRepo.Object, new Mock<IDbContextFactory<ApplicationDbContext>>().Object,
            _userContextFactory.Object, new Mock<IManagementAuthService>().Object, _authState.Object,
            NullLogger<FileBrowserViewModel>.Instance, new Mock<ISearchService>().Object, new Mock<IUserRepository>().Object,
            new FileDownloadTicketStore(), new ZipDownloadTicketStore(), new DemoModeOptions(), _syncRepo.Object,
            shareLinkRepo.Object);
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private string Abs(string path) => Path.Combine(_root, path.Replace('/', Path.DirectorySeparatorChar));

    private void Exists(string path, bool isDirectory)
    {
        if (isDirectory)
            Directory.CreateDirectory(Abs(path));
        else
            File.WriteAllText(Abs(path), "x");
    }

    // ═══════════════════ EnsureFolderAsync ═══════════════════

    [Fact]
    public async Task EnsureFolderAsync_CreatesOnlyMissingFolders_ParentsFirst()
    {
        await _sut.LoadShareAsync("share", "docs");
        Exists("docs/Photos", isDirectory: true);
        var created = new List<string>();
        _fileService
            .Setup(s => s.CreateDirectoryAsync(It.IsAny<string>(), It.IsAny<UserContext>()))
            .Callback<string, UserContext>((path, _) => created.Add(path))
            .Returns(Task.CompletedTask);

        var result = await _sut.EnsureFolderAsync("Photos/2024/Summer");

        Assert.True(result.Success);
        // The existing folder is merged into, never re-created (that would re-stamp its owner).
        Assert.Equal(["docs/Photos/2024", "docs/Photos/2024/Summer"], created);
    }

    [Fact]
    public async Task EnsureFolderAsync_FailsWhenAFileHasTheFolderName()
    {
        await _sut.LoadShareAsync("share");
        Exists("Photos", isDirectory: false);

        var result = await _sut.EnsureFolderAsync("Photos/2024");

        Assert.False(result.Success);
        _fileService.Verify(
            s => s.CreateDirectoryAsync(It.IsAny<string>(), It.IsAny<UserContext>()), Times.Never);
    }

    [Fact]
    public async Task EnsureFolderAsync_Cancelled_Throws_SoTheBatchStopsAsCancelled()
    {
        await _sut.LoadShareAsync("share");

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => _sut.EnsureFolderAsync("Photos", new CancellationToken(canceled: true)));
        _fileService.Verify(
            s => s.CreateDirectoryAsync(It.IsAny<string>(), It.IsAny<UserContext>()), Times.Never);
    }

    // ═══════════════════ Uploads into a sub-folder ═══════════════════

    [Fact]
    public async Task UploadWithConflictHandlingAsync_WritesIntoSubFolder()
    {
        await _sut.LoadShareAsync("share", "docs");

        var outcome = await _sut.UploadWithConflictHandlingAsync(
            "a.txt", new MemoryStream([1, 2, 3]), relativeDir: "Photos/2024");

        Assert.Equal(FileBrowserViewModel.UploadResult.Uploaded, outcome);
        _fileService.Verify(s => s.WriteFileAsync(
            "docs/Photos/2024/a.txt", It.IsAny<Stream>(), It.IsAny<UserContext>(), It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task UploadWithConflictHandlingAsync_ConflictInSubFolder_ShowsRelativePath()
    {
        await _sut.LoadShareAsync("share");
        _fileService
            .Setup(s => s.ReadFileAsync("Photos/a.txt", It.IsAny<UserContext>()))
            .ReturnsAsync(() => new MemoryStream([9, 9, 9]));

        var outcome = await _sut.UploadWithConflictHandlingAsync(
            "a.txt", new MemoryStream([1, 2, 3]), relativeDir: "Photos");

        Assert.Equal(FileBrowserViewModel.UploadResult.Held, outcome);
        var conflict = Assert.Single(_sut.PendingConflicts);
        Assert.Equal("Photos/a.txt", conflict.FileName);
        Assert.Equal("Photos/a.txt", conflict.TargetRelativePath);

        // Clean up the staged temp file.
        await _sut.ResolveConflictAsync(conflict.Id, FileBrowserViewModel.UploadConflictAction.Discard, applyToAll: false);
        Assert.Empty(_sut.PendingConflicts);
    }
}

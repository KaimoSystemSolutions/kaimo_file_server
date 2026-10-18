using System.Security.Claims;
using Kaimo_File_Server.Core.Domain;
using Kaimo_File_Server.Core.Domain.Identity;
using Kaimo_File_Server.Core.Repositories;
using Kaimo_File_Server.Core.Security;
using Kaimo_File_Server.Core.Services;
using Kaimo_File_Server.Core.Services.File;
using Kaimo_File_Server.Infrastructure.Configuration;
using Kaimo_File_Server.Web.Services;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Kaimo_File_Server.Tests;

/// <summary>
/// Anonymous upload links: the upload policy on <see cref="ShareLink"/>, the upload flow of
/// <see cref="PublicUploadService"/> (validation, reservation, never overwriting, releasing a
/// failed reservation) and the extra checks <see cref="ShareLinkService.CreateAsync"/> applies
/// to upload links.
/// </summary>
public sealed class PublicUploadLinkTests
{
    private readonly ShareDefinition _share = new("docs", "/data/docs") { Id = Guid.NewGuid(), IsEnabled = true };
    private readonly Mock<IShareLinkRepository> _links = new();
    private readonly Mock<IShareRepository> _shares = new();
    private readonly Mock<IFileServiceFactory> _fileServices = new();
    private readonly Mock<IFileService> _fs = new();
    private readonly Mock<IUserContextFactory> _contexts = new();
    private readonly Mock<IConfigRepository> _config = new();
    private readonly Mock<IManagementAuthService> _mgmtAuth = new();
    private readonly ShareLinkSettings _settings = new() { AllowUploadLinks = true };
    private readonly ShareLink _link;
    private readonly UserContext _creator;

    public PublicUploadLinkTests()
    {
        _link = new ShareLink
        {
            Token = "up-token", Kind = ShareLinkKind.Upload, ShareId = _share.Id,
            RootRelativePath = "inbox", IsDirectory = true, CreatedByUserId = Guid.NewGuid(),
        };
        _creator = new UserContext(
            new User(_link.CreatedByUserId, "Creator", "creator", "hash", "nt"), [], [], []);

        _shares.Setup(s => s.GetByIdAsync(_share.Id)).ReturnsAsync(_share);
        _fileServices.Setup(f => f.CreateForShare(It.Is<ShareDefinition>(s => s.Id == _share.Id))).Returns(_fs.Object);
        _contexts.Setup(c => c.CreateByUserIdAsync(_link.CreatedByUserId)).ReturnsAsync(_creator);
        _contexts.Setup(c => c.CreateByUsernameAsync("creator")).ReturnsAsync(_creator);
        _config.Setup(c => c.GetAsync(ShareLinkSettings.ConfigKey, It.IsAny<ShareLinkSettings>()))
            .ReturnsAsync(() => _settings);
        _links.Setup(r => r.TryReserveUploadAsync(_link.Token, It.IsAny<long>())).ReturnsAsync(_link);
        _fs.Setup(f => f.ListAsync("inbox", _creator)).ReturnsAsync([]);
    }

    // ---- Policy ----

    [Fact]
    public void ParseExtensions_NormalizesMixedInput()
        => Assert.Equal([".pdf", ".jpg", ".png"], ShareLink.ParseExtensions(" pdf, .JPG;png ;; "));

    [Theory]
    [InlineData("report.PDF", true)]
    [InlineData("photo.jpg", false)]
    [InlineData("noextension", false)]
    public void IsExtensionAllowed_HonoursAllowlist(string name, bool expected)
        => Assert.Equal(expected, new ShareLink { AllowedExtensions = ".pdf" }.IsExtensionAllowed(name));

    [Fact]
    public void IsCurrentlyActive_FalseWhenByteQuotaUsedUp()
    {
        var link = new ShareLink { Kind = ShareLinkKind.Upload, MaxTotalBytes = 100, UploadedBytes = 100 };
        Assert.False(link.IsCurrentlyActive(DateTime.UtcNow));
        link.UploadedBytes = 99;
        Assert.True(link.IsCurrentlyActive(DateTime.UtcNow));
    }

    [Fact]
    public void EffectiveMaxFileSize_LinkCanOnlyLowerGlobalCeiling()
    {
        var settings = new ShareLinkSettings { MaxUploadFileSizeBytes = 1000 };
        Assert.Equal(1000, settings.EffectiveMaxFileSize(new ShareLink()));
        Assert.Equal(500, settings.EffectiveMaxFileSize(new ShareLink { MaxFileSizeBytes = 500 }));
        Assert.Equal(1000, settings.EffectiveMaxFileSize(new ShareLink { MaxFileSizeBytes = 5000 }));
    }

    // ---- Upload flow ----

    [Fact]
    public async Task Upload_WritesIntoLinkFolderAsCreator_WithSizeCappedStream()
    {
        long? cap = null;
        var result = await Uploader().UploadAsync(_link, "a.txt", 3, max => { cap = max; return new MemoryStream(new byte[3]); });

        Assert.Equal(PublicUploadStatus.Uploaded, result.Status);
        Assert.Equal(3, cap);
        _fs.Verify(f => f.WriteFileAsync("inbox/a.txt", It.IsAny<Stream>(), _creator, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Upload_StripsClientPath()
    {
        await Uploader().UploadAsync(_link, @"..\..\etc\evil.txt", 1, _ => new MemoryStream(new byte[1]));
        _fs.Verify(f => f.WriteFileAsync("inbox/evil.txt", It.IsAny<Stream>(), _creator, It.IsAny<CancellationToken>()), Times.Once);
    }

    // ---- Folder uploads ----
    // Folder existence is probed on disk, so these tests map share paths into a temp folder
    // and let the mocked file service create and delete real directories there.

    private sealed class DiskShare : IDisposable
    {
        public string Root { get; } = Directory.CreateTempSubdirectory("kaimo-public-upload-").FullName;
        public string Abs(string path) => Path.Combine(Root, path.Replace('/', Path.DirectorySeparatorChar));
        public void Dispose() => Directory.Delete(Root, recursive: true);
    }

    private DiskShare UseDisk()
    {
        var disk = new DiskShare();
        Directory.CreateDirectory(disk.Abs("inbox"));
        _fs.Setup(f => f.ToAbsolutePath(It.IsAny<string>())).Returns<string>(disk.Abs);
        _fs.Setup(f => f.CreateDirectoryAsync(It.IsAny<string>(), _creator))
            .Callback<string, UserContext>((path, _) => Directory.CreateDirectory(disk.Abs(path)))
            .Returns(Task.CompletedTask);
        _fs.Setup(f => f.DeleteFileAsync(It.IsAny<string>(), _creator, false))
            .Callback<string, UserContext, bool>((path, _, _) => Directory.Delete(disk.Abs(path)))
            .Returns(Task.CompletedTask);
        // Like the real FileService: a missing path is reported as a file, never as missing.
        _fs.Setup(f => f.GetMetadataAsync(It.IsAny<string>(), It.IsAny<UserContext>()))
            .ReturnsAsync(new FileMetadata { IsDirectory = false });
        return disk;
    }

    [Fact]
    public async Task Upload_IntoSubFolder_CreatesMissingFoldersBelowLinkFolder()
    {
        using var disk = UseDisk();
        _fs.Setup(f => f.ListAsync("inbox/Photos/2024", _creator)).ReturnsAsync([]);

        var result = await Uploader().UploadAsync(
            _link, "a.jpg", 1, _ => new MemoryStream(new byte[1]), relativeDir: "Photos/2024");

        Assert.Equal(PublicUploadStatus.Uploaded, result.Status);
        Assert.Equal("Photos/2024/a.jpg", result.StoredName);
        _fs.Verify(f => f.CreateDirectoryAsync("inbox/Photos", _creator), Times.Once);
        _fs.Verify(f => f.CreateDirectoryAsync("inbox/Photos/2024", _creator), Times.Once);
        _fs.Verify(f => f.WriteFileAsync("inbox/Photos/2024/a.jpg", It.IsAny<Stream>(), _creator, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Upload_IntoSubFolder_FailedWrite_RemovesOnlyTheFoldersItCreated()
    {
        using var disk = UseDisk();
        Directory.CreateDirectory(disk.Abs("inbox/Photos"));
        _fs.Setup(f => f.WriteFileAsync(It.IsAny<string>(), It.IsAny<Stream>(), _creator, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new OperationCanceledException());

        var result = await Uploader().UploadAsync(
            _link, "a.jpg", 1, _ => new MemoryStream(new byte[1]), relativeDir: "Photos/2024/Summer");

        Assert.Equal(PublicUploadStatus.Cancelled, result.Status);
        // A cancelled upload must not leave free folders behind; the pre-existing one stays.
        Assert.False(Directory.Exists(disk.Abs("inbox/Photos/2024")));
        Assert.True(Directory.Exists(disk.Abs("inbox/Photos")));
        _fs.Verify(f => f.DeleteFileAsync("inbox/Photos", It.IsAny<UserContext>(), It.IsAny<bool>()), Times.Never);
    }

    [Fact]
    public async Task Upload_IntoSubFolder_FailedWrite_KeepsFoldersThatGainedContent()
    {
        using var disk = UseDisk();
        _fs.Setup(f => f.WriteFileAsync(It.IsAny<string>(), It.IsAny<Stream>(), _creator, It.IsAny<CancellationToken>()))
            .Callback(() => File.WriteAllText(disk.Abs("inbox/Photos/other.jpg"), "x")) // another upload landed
            .ThrowsAsync(new IOException("disk full"));

        var result = await Uploader().UploadAsync(
            _link, "a.jpg", 1, _ => new MemoryStream(new byte[1]), relativeDir: "Photos/2024");

        Assert.Equal(PublicUploadStatus.Failed, result.Status);
        Assert.False(Directory.Exists(disk.Abs("inbox/Photos/2024")));
        Assert.True(File.Exists(disk.Abs("inbox/Photos/other.jpg")));
    }

    [Fact]
    public async Task Upload_IntoExistingSubFolder_MergesAndNeverOverwrites()
    {
        using var disk = UseDisk();
        Directory.CreateDirectory(disk.Abs("inbox/Photos"));
        _fs.Setup(f => f.ListAsync("inbox/Photos", _creator)).ReturnsAsync([new FileMetadata { Name = "a.jpg" }]);

        var result = await Uploader().UploadAsync(
            _link, "a.jpg", 1, _ => new MemoryStream(new byte[1]), relativeDir: "Photos");

        Assert.Equal("Photos/a (2).jpg", result.StoredName);
        _fs.Verify(f => f.CreateDirectoryAsync(It.IsAny<string>(), It.IsAny<UserContext>()), Times.Never);
        _fs.Verify(f => f.WriteFileAsync("inbox/Photos/a (2).jpg", It.IsAny<Stream>(), _creator, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Theory]
    [InlineData("..")]
    [InlineData("../outside")]
    [InlineData("a/../../b")]
    [InlineData("a/./b")]
    [InlineData("1/2/3/4/5/6/7/8/9/10/11/12/13/14/15/16/17")]
    public async Task Upload_InvalidSubFolder_IsRejectedWithoutReservingOrWriting(string relativeDir)
    {
        var result = await Uploader().UploadAsync(_link, "a.txt", 1, _ => Stream.Null, relativeDir: relativeDir);

        Assert.Equal(PublicUploadStatus.InvalidName, result.Status);
        _links.Verify(r => r.TryReserveUploadAsync(It.IsAny<string>(), It.IsAny<long>()), Times.Never);
        _fs.Verify(f => f.CreateDirectoryAsync(It.IsAny<string>(), It.IsAny<UserContext>()), Times.Never);
        _fs.Verify(f => f.WriteFileAsync(It.IsAny<string>(), It.IsAny<Stream>(), It.IsAny<UserContext>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    // Visitors on Linux/macOS may send names Windows cannot use; they are renamed, not refused.
    [Fact]
    public async Task Upload_WindowsInvalidNames_AreRenamed()
    {
        using var disk = UseDisk();
        _fs.Setup(f => f.ListAsync("inbox/bad_dir/trailing", _creator)).ReturnsAsync([]);

        var result = await Uploader().UploadAsync(
            _link, "a:b.txt", 1, _ => new MemoryStream(new byte[1]), relativeDir: "bad:dir/trailing.");

        Assert.Equal(PublicUploadStatus.Uploaded, result.Status);
        Assert.Equal("bad_dir/trailing/a_b.txt", result.StoredName);
        _fs.Verify(f => f.WriteFileAsync("inbox/bad_dir/trailing/a_b.txt", It.IsAny<Stream>(), _creator, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Upload_SubFolderNameTakenByFile_FailsAndReleasesReservation()
    {
        using var disk = UseDisk();
        File.WriteAllText(disk.Abs("inbox/Photos"), "x");

        var result = await Uploader().UploadAsync(
            _link, "a.jpg", 3, _ => new MemoryStream(new byte[3]), relativeDir: "Photos");

        Assert.Equal(PublicUploadStatus.Failed, result.Status);
        _links.Verify(r => r.ReleaseUploadAsync(_link.Id, 3), Times.Once);
        _fs.Verify(f => f.WriteFileAsync(It.IsAny<string>(), It.IsAny<Stream>(), It.IsAny<UserContext>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Upload_NeverOverwrites_PicksNextFreeName()
    {
        _fs.Setup(f => f.ListAsync("inbox", _creator)).ReturnsAsync(
            [new FileMetadata { Name = "a.txt" }, new FileMetadata { Name = "a (2).txt" }]);

        var result = await Uploader().UploadAsync(_link, "a.txt", 1, _ => new MemoryStream(new byte[1]));

        Assert.Equal("a (3).txt", result.StoredName);
        _fs.Verify(f => f.WriteFileAsync("inbox/a (3).txt", It.IsAny<Stream>(), _creator, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Upload_RejectsDisallowedTypeAndOversize_WithoutReserving()
    {
        _link.AllowedExtensions = ".pdf";
        _link.MaxFileSizeBytes = 10;
        var sut = Uploader();

        Assert.Equal(PublicUploadStatus.TypeNotAllowed, (await sut.UploadAsync(_link, "x.exe", 1, _ => Stream.Null)).Status);
        Assert.Equal(PublicUploadStatus.TooLarge, (await sut.UploadAsync(_link, "x.pdf", 11, _ => Stream.Null)).Status);
        _links.Verify(r => r.TryReserveUploadAsync(It.IsAny<string>(), It.IsAny<long>()), Times.Never);
    }

    [Fact]
    public async Task Upload_RefusedWhenUploadLinksDisabled_OrForDownloadLink()
    {
        Assert.Equal(PublicUploadStatus.Unavailable,
            (await Uploader().UploadAsync(new ShareLink { Token = "d" }, "a.txt", 1, _ => Stream.Null)).Status);

        _settings.AllowUploadLinks = false;
        Assert.Equal(PublicUploadStatus.Unavailable,
            (await Uploader().UploadAsync(_link, "a.txt", 1, _ => Stream.Null)).Status);
    }

    [Fact]
    public async Task Upload_QuotaExhausted_ReportsQuotaExceeded()
    {
        _links.Setup(r => r.TryReserveUploadAsync(_link.Token, It.IsAny<long>())).ReturnsAsync((ShareLink?)null);
        _links.Setup(r => r.GetByTokenAsync(_link.Token)).ReturnsAsync(_link);

        var result = await Uploader().UploadAsync(_link, "a.txt", 1, _ => Stream.Null);

        Assert.Equal(PublicUploadStatus.QuotaExceeded, result.Status);
        _fs.Verify(f => f.WriteFileAsync(It.IsAny<string>(), It.IsAny<Stream>(), It.IsAny<UserContext>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Upload_FailedWrite_ReleasesReservation()
    {
        _fs.Setup(f => f.WriteFileAsync(It.IsAny<string>(), It.IsAny<Stream>(), _creator, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new UnauthorizedAccessException());

        var result = await Uploader().UploadAsync(_link, "a.txt", 7, _ => new MemoryStream(new byte[7]));

        Assert.Equal(PublicUploadStatus.Failed, result.Status);
        _links.Verify(r => r.ReleaseUploadAsync(_link.Id, 7), Times.Once);
    }

    [Fact]
    public async Task Upload_DisabledCreator_IsUnavailable()
    {
        _contexts.Setup(c => c.CreateByUserIdAsync(_link.CreatedByUserId)).ReturnsAsync(new UserContext(
            new User(_link.CreatedByUserId, "Creator", "creator", "hash", "nt", isEnabled: false), [], [], []));

        var result = await Uploader().UploadAsync(_link, "a.txt", 1, _ => Stream.Null);

        Assert.Equal(PublicUploadStatus.Unavailable, result.Status);
    }

    // ---- Creation ----

    [Fact]
    public async Task Create_UploadLink_RequiresUploadPermissionFolderAndWriteAccess()
    {
        _mgmtAuth.Setup(m => m.CanManageShareAsync(_creator, _share.Id, ManagementPermission.ManageUploadLinks)).ReturnsAsync(true);
        _mgmtAuth.Setup(m => m.CanManageShareAsync(_creator, _share.Id, ManagementPermission.ManageShareLinks)).ReturnsAsync(false);
        _fs.Setup(f => f.CanCreateAsync("inbox/upload.probe", _creator)).ReturnsAsync(true);
        _links.Setup(r => r.CreateAsync(It.IsAny<ShareLink>())).ReturnsAsync((ShareLink l) => l);
        var service = LinkService();

        var created = await service.CreateAsync(Request(isDirectory: true));
        Assert.NotNull(created);
        Assert.Equal(ShareLinkKind.Upload, created!.Kind);
        Assert.Equal(".pdf;.jpg", created.AllowedExtensions);

        Assert.Null(await service.CreateAsync(Request(isDirectory: false)));   // file target
        Assert.Null(await service.CreateAsync(Request(isDirectory: true) with { Kind = ShareLinkKind.Download })); // no download right

        _fs.Setup(f => f.CanCreateAsync("inbox/upload.probe", _creator)).ReturnsAsync(false);
        Assert.Null(await service.CreateAsync(Request(isDirectory: true)));   // creator cannot write

        _fs.Setup(f => f.CanCreateAsync("inbox/upload.probe", _creator)).ReturnsAsync(true);
        _settings.AllowUploadLinks = false;
        Assert.Null(await service.CreateAsync(Request(isDirectory: true)));   // globally disabled
    }

    private CreateShareLinkRequest Request(bool isDirectory) => new(
        _share.Id, "inbox", isDirectory, "Inbox", null, null, null, null, null, null,
        Kind: ShareLinkKind.Upload, AllowedExtensions: "pdf, JPG");

    private ShareLinkService LinkService()
    {
        var auth = new Mock<AuthenticationStateProvider>();
        auth.Setup(a => a.GetAuthenticationStateAsync()).ReturnsAsync(new AuthenticationState(
            new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.Name, "creator")], "test"))));
        return new ShareLinkService(
            _links.Object, _config.Object, Mock.Of<IPasswordService>(), new LoginThrottle(),
            _mgmtAuth.Object, _contexts.Object, auth.Object, new PublicDownloadTicketStore(),
            shares: _shares.Object, fileServices: _fileServices.Object);
    }

    private PublicUploadService Uploader()
    {
        var shareLinks = new ShareLinkService(
            _links.Object, _config.Object, Mock.Of<IPasswordService>(), new LoginThrottle(),
            _mgmtAuth.Object, _contexts.Object, Mock.Of<AuthenticationStateProvider>(),
            new PublicDownloadTicketStore());
        return new PublicUploadService(
            _links.Object, _shares.Object, _fileServices.Object, shareLinks,
            NullLogger<PublicUploadService>.Instance);
    }
}

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
        _fileServices.Setup(f => f.CreateForShare(_share.Id, _share.Path)).Returns(_fs.Object);
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

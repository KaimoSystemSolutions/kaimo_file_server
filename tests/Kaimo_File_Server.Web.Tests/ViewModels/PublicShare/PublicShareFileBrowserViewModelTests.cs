using Kaimo_File_Server.Core.Domain;
using Kaimo_File_Server.Web.Components.ViewModels;
using Kaimo_File_Server.Web.Services;
using Moq;
using Xunit;

namespace Kaimo_File_Server.Web.Tests.ViewModels.PublicShare;

/// <summary>
/// Pins that an anonymous public share link never reveals internal information (neither the
/// sync configuration of the shared folder nor the real share name), that an in-page preview is
/// an access like a download, and that every listing re-validates the link so a revoked link
/// stops serving immediately and the page switches to "unavailable".
/// </summary>
public class PublicShareFileBrowserViewModelTests
{
    private readonly PublicShareFixture _f = new();
    private readonly PublicShareFileBrowserViewModel _sut;
    private readonly Guid _visit = Guid.NewGuid();
    private int _unavailable;

    private static readonly FileMetadata SharedFile = new()
    {
        Path = "/data/internal-share/projects/secret/a.txt",
        Name = "a.txt",
        Size = PublicShareFixture.Content.Length,
    };

    public PublicShareFileBrowserViewModelTests()
    {
        _sut = _f.CreateViewModel();
        _sut.Initialize(_f.Link, _visit, "files.example.com");
        _sut.LinkUnavailable += () => _unavailable++;
    }

    [Fact]
    public async Task LoadShareAsync_NeverLoadsSyncState()
    {
        await _sut.LoadShareAsync(_f.Share.Name);

        Assert.Null(_sut.ErrorMessage);
        Assert.False(_sut.CanManageSyncs);
        Assert.Null(_sut.GetSyncMarker(new FileMetadata { Path = "/data/internal-share/projects/secret/a" }));
        _f.SyncRepo.Verify(r => r.GetAllAsync(It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task CurrentBrowserShare_ExposesOnlyTheLinkDisplayName()
    {
        await _sut.LoadShareAsync(_f.Share.Name);

        Assert.Equal("Public folder", _sut.CurrentBrowserShare?.Name);
    }

    [Fact]
    public async Task Listing_DoesNotConsumeAnAccess()
    {
        await _sut.LoadShareAsync(_f.Share.Name);
        await _sut.LoadShareAsync(_f.Share.Name, "sub");

        Assert.Equal(0, _f.Link.AccessCount);
        Assert.Equal(0, _unavailable);
    }

    // ── Preview ──

    [Fact]
    public async Task Preview_ConsumesOneAccess_AndReturnsTheContent()
    {
        await _sut.LoadShareAsync(_f.Share.Name);

        var preview = await _sut.ReadFileForPreviewAsync(SharedFile);

        Assert.Equal(PublicShareFixture.Content, preview!.Value.Data);
        Assert.Equal(1, _f.Link.AccessCount);
    }

    [Fact]
    public async Task Preview_OfTheLastAccess_StillShowsTheFile()
    {
        _f.Link.MaxAccessCount = 1;
        await _sut.LoadShareAsync(_f.Share.Name);

        Assert.NotNull(await _sut.ReadFileForPreviewAsync(SharedFile));
        Assert.Equal(0, _unavailable);
    }

    [Fact]
    public async Task Preview_ExhaustedLink_ReturnsNothing_AndSignalsUnavailable()
    {
        _f.Link.MaxAccessCount = 1;
        await _sut.LoadShareAsync(_f.Share.Name);
        await _sut.ReadFileForPreviewAsync(SharedFile);

        Assert.Null(await _sut.ReadFileForPreviewAsync(SharedFile));
        Assert.Equal(1, _f.Reads);
        Assert.Equal(1, _unavailable);
    }

    [Theory]
    [InlineData("disabled")]
    [InlineData("expired")]
    [InlineData("deleted")]
    [InlineData("downloadsOff")]
    [InlineData("creatorDisabled")]
    [InlineData("shareDisabled")]
    public async Task Preview_AfterRevocation_ReturnsNothing_AndSignalsUnavailable(string how)
    {
        await _sut.LoadShareAsync(_f.Share.Name);
        _f.Revoke(how);

        Assert.Null(await _sut.ReadFileForPreviewAsync(SharedFile));
        Assert.Equal(0, _f.Reads);
        Assert.Equal(1, _unavailable);
    }

    [Fact]
    public async Task Preview_OutsideTheLinkRoot_IsRejectedWithoutConsuming()
    {
        await _sut.LoadShareAsync(_f.Share.Name);

        var outside = new FileMetadata { Path = "/data/internal-share/projects/other.txt", Name = "other.txt", Size = 3 };

        Assert.Null(await _sut.ReadFileForPreviewAsync(outside));
        Assert.Equal(0, _f.Link.AccessCount);
        Assert.Equal(0, _f.Reads);
        Assert.Equal(0, _unavailable);
    }

    // Nothing could be shown, so the visitor would lose an access (on a "max. 1" link: the only
    // one, leaving the download button dead) for a preview that never appears.
    [Theory]
    [InlineData("setup.zip")]
    [InlineData("tool.exe")]
    [InlineData("data.bin")]
    public async Task Preview_OfAnUnsupportedType_IsSkippedWithoutConsuming(string name)
    {
        _f.Link.MaxAccessCount = 1;
        await _sut.LoadShareAsync(_f.Share.Name);

        var file = new FileMetadata { Path = $"/data/internal-share/projects/secret/{name}", Name = name, Size = 3 };

        Assert.Null(await _sut.ReadFileForPreviewAsync(file));
        Assert.Equal(0, _f.Link.AccessCount);
        Assert.Equal(0, _f.Reads);
        Assert.Equal(0, _f.Notified);
        Assert.Equal(0, _unavailable);
        Assert.NotNull(await _sut.GetDownloadUrlAsync(file));
    }

    [Fact]
    public async Task Preview_IgnoresTheBandwidthCap()
    {
        // One byte per second would take far longer than the timeout if the preview were throttled.
        _f.Link.MaxBytesPerSecond = 1;
        await _sut.LoadShareAsync(_f.Share.Name);

        var preview = await _sut.ReadFileForPreviewAsync(SharedFile).WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(PublicShareFixture.Content, preview!.Value.Data);
    }

    // ── Hashing ──

    // Hashing reads the whole file (any size) on the server without counting an access or
    // honouring the bandwidth cap, so the anonymous browser does not offer it at all.
    [Fact]
    public void Capabilities_DoNotOfferHashing()
        => Assert.False(_sut.Capabilities.CanHash);

    [Fact]
    public async Task ComputeFileHash_IsRefused_WithoutReadingTheFile()
    {
        await _sut.LoadShareAsync(_f.Share.Name);

        Assert.Null(await _sut.ComputeFileHashAsync(SharedFile, System.Security.Cryptography.HashAlgorithmName.SHA256));
        Assert.Equal(0, _f.Reads);
        Assert.Equal(0, _f.Link.AccessCount);
    }

    // ── Revalidation on navigation ──

    [Theory]
    [InlineData("disabled")]
    [InlineData("expired")]
    [InlineData("deleted")]
    [InlineData("exhausted")]
    [InlineData("creatorDisabled")]
    public async Task Navigating_AfterRevocation_SignalsUnavailable_AndListsNothing(string how)
    {
        await _sut.LoadShareAsync(_f.Share.Name);
        _f.Revoke(how);

        await _sut.LoadShareAsync(_f.Share.Name, "sub");

        Assert.Equal(1, _unavailable);
        Assert.Empty(_sut.Items);
    }

    [Fact]
    public async Task Refresh_AfterRevocation_SignalsUnavailable()
    {
        await _sut.LoadShareAsync(_f.Share.Name);
        _f.Revoke("disabled");

        await _sut.RefreshCurrentDirectoryAsync();

        Assert.Equal(1, _unavailable);
    }

    [Fact]
    public async Task DownloadUrl_AfterRevocation_IsNotIssued()
    {
        await _sut.LoadShareAsync(_f.Share.Name);
        _f.Revoke("disabled");

        Assert.Null(await _sut.GetDownloadUrlAsync(SharedFile));
        Assert.Equal(1, _unavailable);
    }

    // ── "Link accessed" once per visit ──

    [Fact]
    public async Task Preview_NotifiesOncePerVisit()
    {
        await _sut.LoadShareAsync(_f.Share.Name);

        await _sut.ReadFileForPreviewAsync(SharedFile);
        await _sut.ReadFileForPreviewAsync(SharedFile);

        Assert.Equal(1, _f.Notified);
    }

    [Fact]
    public async Task Preview_InANewVisit_NotifiesAgain()
    {
        await _sut.LoadShareAsync(_f.Share.Name);
        await _sut.ReadFileForPreviewAsync(SharedFile);

        var second = _f.CreateViewModel();
        second.Initialize(_f.Link, Guid.NewGuid(), "files.example.com");
        await second.LoadShareAsync(_f.Share.Name);
        await second.ReadFileForPreviewAsync(SharedFile);

        Assert.Equal(2, _f.Notified);
    }

    [Fact]
    public async Task DownloadTicket_CarriesTheVisit()
    {
        await _sut.LoadShareAsync(_f.Share.Name);

        var url = await _sut.GetDownloadUrlAsync(SharedFile);
        var ticket = Uri.UnescapeDataString(url!.Split("ticket=")[1]);

        Assert.True(_f.Tickets.TryConsume(ticket, out var download));
        Assert.Equal(_visit, download!.VisitId);
    }
}

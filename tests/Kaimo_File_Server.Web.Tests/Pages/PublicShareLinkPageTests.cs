using System.Globalization;
using Bunit;
using Kaimo_File_Server.Core.Domain;
using Kaimo_File_Server.Core.Security.Demo;
using Kaimo_File_Server.Web.Components.Pages.PublicShare;
using Kaimo_File_Server.Web.Services;
using Microsoft.AspNetCore.Components.Forms;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;
using Kaimo_File_Server.Web.Tests.Services;

namespace Kaimo_File_Server.Web.Tests.Pages;

/// <summary>
/// The public landing page shows "unavailable" for a used-up link right away (download and
/// upload links alike), and an upload page re-checks its link before every new selection.
/// </summary>
public sealed class PublicShareLinkPageTests : IDisposable
{
    private readonly PublicShareFixture _f = new();
    private readonly BunitContext _ctx = new();
    private readonly CultureInfo _previousCulture = CultureInfo.CurrentUICulture;

    public PublicShareLinkPageTests()
    {
        CultureInfo.CurrentUICulture = new CultureInfo("en");
        _ctx.JSInterop.Mode = JSRuntimeMode.Loose;

        var env = new Mock<Microsoft.AspNetCore.Hosting.IWebHostEnvironment>();
        env.SetupGet(e => e.WebRootPath).Returns(MailNotificationTests.FindWebRoot());
        _ctx.Services.AddSingleton(new AssetProvider(env.Object));

        var shareLinks = _f.CreateShareLinkService();
        _ctx.Services.AddSingleton(shareLinks);
        _ctx.Services.AddSingleton(new ClientConnectionInfo { Host = "files.example.com", RemoteAddress = "10.0.0.5" });
        _ctx.Services.AddSingleton(new PublicUploadService(_f.Links.Object, _f.Shares.Object, _f.FileServices.Object,
            shareLinks, NullLogger<PublicUploadService>.Instance, new DemoModeOptions()));
        _ctx.Services.AddSingleton(_f.Shares.Object);
        _ctx.Services.AddSingleton(_f.CreateViewModel());

        _f.Link.IsDirectory = false;
    }

    public void Dispose()
    {
        _ctx.Dispose();
        CultureInfo.CurrentUICulture = _previousCulture;
    }

    private IRenderedComponent<PublicShareLink> Render()
        => _ctx.Render<PublicShareLink>(p => p.Add(c => c.Token, PublicShareFixture.Token));

    private static bool ShowsUnavailable(IRenderedComponent<PublicShareLink> cut)
        => cut.Markup.Contains("Link unavailable");

    [Fact]
    public void ActiveDownloadLink_ShowsTheDownload()
    {
        var cut = Render();

        Assert.False(ShowsUnavailable(cut));
        Assert.Contains("Public folder", cut.Markup);
    }

    [Fact]
    public void UsedUpDownloadLink_ShowsUnavailable()
    {
        _f.Revoke("exhausted");

        Assert.True(ShowsUnavailable(Render()));
    }

    [Fact]
    public void UploadLink_WithAllFilesUsed_ShowsUnavailable()
    {
        MakeUploadLink();
        _f.Link.MaxAccessCount = 3;
        _f.Link.AccessCount = 3;

        Assert.True(ShowsUnavailable(Render()));
    }

    [Fact]
    public void UploadLink_WithQuotaUsedUp_ShowsUnavailable()
    {
        MakeUploadLink();
        _f.Link.MaxTotalBytes = 100;
        _f.Link.UploadedBytes = 100;

        Assert.True(ShowsUnavailable(Render()));
    }

    [Theory]
    [InlineData("exhausted")]
    [InlineData("disabled")]
    [InlineData("expired")]
    public void UploadLink_RevokedDuringTheVisit_NextSelectionShowsUnavailable(string how)
    {
        MakeUploadLink();
        var cut = Render();
        Assert.False(ShowsUnavailable(cut));

        _f.Revoke(how);
        cut.FindComponent<InputFile>().UploadFiles(InputFileContent.CreateFromText("hello", "a.txt"));

        cut.WaitForAssertion(() => Assert.True(ShowsUnavailable(cut)));
        _f.Links.Verify(r => r.TryReserveUploadAsync(It.IsAny<string>(), It.IsAny<long>()), Times.Never);
    }

    private void MakeUploadLink()
    {
        _f.Link.Kind = ShareLinkKind.Upload;
        _f.Link.IsDirectory = true;
    }
}

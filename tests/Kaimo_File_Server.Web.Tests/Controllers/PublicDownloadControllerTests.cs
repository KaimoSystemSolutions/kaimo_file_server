using Kaimo_File_Server.Core.Helpers;
using Kaimo_File_Server.Web.Controllers;
using Kaimo_File_Server.Web.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Xunit;

namespace Kaimo_File_Server.Web.Tests.Controllers;

/// <summary>
/// The anonymous download endpoint after the shared access policy was extracted: it still
/// consumes one access per download, keeps the bandwidth cap, stays inside the link root and
/// works in the read-only demo. "Link accessed" is published once per visit; a ticket without
/// a visit (issued before the upgrade) notifies on every download as before.
/// </summary>
public sealed class PublicDownloadControllerTests
{
    private readonly PublicShareFixture _f = new();
    private readonly PublicDownloadController _sut;

    public PublicDownloadControllerTests()
    {
        _sut = new PublicDownloadController(_f.Tickets, _f.CreatePolicy(), _f.FileServices.Object)
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() },
        };
        _sut.HttpContext.Request.Host = new HostString("files.example.com");
    }

    private string Ticket(Guid? visitId = null, string path = "projects/secret/a.txt")
        => _f.Tickets.Issue(new PublicDownloadTicket(PublicShareFixture.Token, [path], "a.txt", Zip: false, visitId));

    [Fact]
    public async Task Download_ServesFile_ConsumesOneAccess_AndNotifies()
    {
        var result = await _sut.Download(Ticket(), CancellationToken.None);

        var file = Assert.IsType<FileStreamResult>(result);
        Assert.Equal("a.txt", file.FileDownloadName);
        Assert.Equal(1, _f.Link.AccessCount);
        Assert.Equal(1, _f.Notified);
    }

    [Fact]
    public async Task Download_TicketIsSingleUse()
    {
        var ticket = Ticket();
        await _sut.Download(ticket, CancellationToken.None);

        Assert.IsType<BadRequestObjectResult>(await _sut.Download(ticket, CancellationToken.None));
    }

    [Fact]
    public async Task Download_SameVisitTwice_NotifiesOnce()
    {
        var visit = Guid.NewGuid();

        await _sut.Download(Ticket(visit), CancellationToken.None);
        await _sut.Download(Ticket(visit), CancellationToken.None);

        Assert.Equal(2, _f.Link.AccessCount);
        Assert.Equal(1, _f.Notified);
    }

    [Fact]
    public async Task Download_LegacyTicketWithoutVisit_NotifiesEveryTime()
    {
        await _sut.Download(Ticket(), CancellationToken.None);
        await _sut.Download(Ticket(), CancellationToken.None);

        Assert.Equal(2, _f.Notified);
    }

    [Fact]
    public async Task Download_ExhaustedLink_IsNotFound()
    {
        _f.Revoke("exhausted");

        Assert.IsType<NotFoundResult>(await _sut.Download(Ticket(), CancellationToken.None));
        Assert.Equal(0, _f.Notified);
    }

    [Theory]
    [InlineData("disabled")]
    [InlineData("downloadsOff")]
    [InlineData("creatorDisabled")]
    [InlineData("shareDisabled")]
    public async Task Download_RevokedLink_IsNotFound(string how)
    {
        _f.Revoke(how);

        Assert.IsType<NotFoundResult>(await _sut.Download(Ticket(), CancellationToken.None));
    }

    [Fact]
    public async Task Download_PathOutsideLinkRoot_IsForbidden()
    {
        Assert.IsType<ForbidResult>(await _sut.Download(Ticket(path: "projects/other/a.txt"), CancellationToken.None));
        Assert.Equal(0, _f.Reads);
    }

    [Fact]
    public async Task Download_InvalidPath_IsBadRequest()
    {
        Assert.IsType<BadRequestObjectResult>(
            await _sut.Download(Ticket(path: "projects/secret/../x"), CancellationToken.None));
    }

    [Fact]
    public async Task Download_AppliesTheBandwidthCap()
    {
        _f.Link.MaxBytesPerSecond = 1024;

        var file = Assert.IsType<FileStreamResult>(await _sut.Download(Ticket(), CancellationToken.None));

        Assert.IsType<RateLimitedStream>(file.FileStream);
    }

    [Fact]
    public async Task Download_ReadOnlyDemo_ServesWithoutConsuming()
    {
        _f.DemoReadOnly = true;

        Assert.IsType<FileStreamResult>(await _sut.Download(Ticket(), CancellationToken.None));
        Assert.Equal(0, _f.Link.AccessCount);
    }
}

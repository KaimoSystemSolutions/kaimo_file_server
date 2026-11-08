using System.Security.Claims;
using System.Text;
using Kaimo_File_Server.Core.Domain;
using Kaimo_File_Server.Core.Repositories;
using Kaimo_File_Server.Core.Security;
using Kaimo_File_Server.Core.Services;
using Kaimo_File_Server.Search;
using Kaimo_File_Server.Web.Controllers.Api;
using Kaimo_File_Server.Web.Tests.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Kaimo_File_Server.Web.Tests.Controllers.Api;

/// <summary>The client API over a standalone virtual share, next to a real local share.</summary>
public sealed class BrowseApiVirtualShareTests : IDisposable
{
    private readonly MountTestHarness _h = new();
    private readonly BrowseApiController _sut;

    public BrowseApiVirtualShareTests()
    {
        _h.Remote.Directories.Add("root/docs");
        _h.Remote.Files["root/a.txt"] = "hello"u8.ToArray();
        _h.Remote.Files["root/docs/report.txt"] = "12345"u8.ToArray();
        File.WriteAllText(Path.Combine(_h.HostPath, "Team", "local.txt"), "local");
        _sut = NewController();
    }

    public void Dispose() => _h.Dispose();

    private Guid Id => _h.Standalone.Id;

    private Guid HostId => _h.Host.Id;

    private DefaultHttpContext NewContext() => new()
    {
        User = new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim(ClaimTypes.NameIdentifier, _h.User.User.Id.ToString())], "test"))
    };

    private Mock<IUserContextFactory> NewUsers()
    {
        var users = new Mock<IUserContextFactory>();
        users.Setup(f => f.CreateByUserIdAsync(_h.User.User.Id)).ReturnsAsync(_h.User);
        return users;
    }

    private BrowseApiController NewController()
    {
        var shares = new Mock<IShareRepository>();
        shares.Setup(r => r.GetByIdAsync(_h.Host.Id)).ReturnsAsync(_h.Host);
        shares.Setup(r => r.GetAllEnabledAsync()).ReturnsAsync([_h.Host]);
        return new BrowseApiController(NewUsers().Object, shares.Object, _h.Factory.Object, null!,
            Mock.Of<IManagementAuthService>(), _h.NewRouter(), NullLogger<BrowseApiController>.Instance)
        {
            ControllerContext = new ControllerContext { HttpContext = NewContext() }
        };
    }

    private static int Status(IActionResult r) => r switch
    {
        ObjectResult o => o.StatusCode ?? 200,
        StatusCodeResult s => s.StatusCode,
        _ => 200
    };

    private static string? Code(IActionResult r) => ((r as ObjectResult)?.Value as ApiError)?.Code;

    private static List<FileEntryDto> Entries(IActionResult r)
        => Assert.IsType<List<FileEntryDto>>(Assert.IsType<OkObjectResult>(r).Value);

    private static List<ShareDto> Shares(IActionResult r)
        => Assert.IsType<List<ShareDto>>(Assert.IsType<OkObjectResult>(r).Value);

    private string Local(string rel) => Path.Combine(_h.HostPath, rel.Replace('/', Path.DirectorySeparatorChar));

    // ── share list ───────────────────────────────────────────────────

    [Fact]
    public async Task Shares_ListTheVirtualShare_Marked()
    {
        var shares = Shares(await _sut.GetShares());

        var share = Assert.Single(shares, s => s.Id == Id);
        Assert.Equal("Archiv", share.Name);
        Assert.True(share.IsVirtual);
        Assert.False(share.IsRecycleEnabled);
        Assert.False(Assert.Single(shares, s => s.Id == HostId).IsVirtual);
    }

    [Fact]
    public async Task Shares_LeaveOutMountedShares()
        => Assert.DoesNotContain(Shares(await _sut.GetShares()), s => s.Id == _h.Share.Id);

    [Fact]
    public async Task Shares_WithoutGrant_LeaveItOut()
    {
        _h.Granted = null;
        Assert.DoesNotContain(Shares(await NewController().GetShares()), s => s.Id == Id);
    }

    [Fact]
    public async Task Shares_ConnectionNotReady_LeaveItOut()
    {
        _h.Connection.State = StorageConnectionState.Disabled;
        Assert.DoesNotContain(Shares(await NewController().GetShares()), s => s.Id == Id);
    }

    // ── browsing ─────────────────────────────────────────────────────

    [Fact]
    public async Task List_OfTheRoot_ShowsRemoteItems_MarkedVirtual()
    {
        var entries = Entries(await _sut.List(Id, null));

        Assert.Equal(["a.txt", "docs"], entries.Select(e => e.Path).Order());
        Assert.All(entries, e => Assert.True(e.IsVirtualFolder));
    }

    [Fact]
    public async Task List_WithoutGrant_IsNotFound()
    {
        _h.Granted = null;
        Assert.Equal(404, Status(await NewController().List(Id, null)));
    }

    [Fact]
    public async Task List_OfAMountedShareById_IsNotFound()
        => Assert.Equal(404, Status(await _sut.List(_h.Share.Id, null)));

    [Fact]
    public async Task List_ConnectionNotReady_IsUnavailable()
    {
        _h.Connection.State = StorageConnectionState.Disabled;
        var result = await NewController().List(Id, null);
        Assert.Equal(503, Status(result));
        Assert.Equal("remote_unavailable", Code(result));
    }

    [Fact]
    public async Task Download_ReadsTheRemoteFile()
    {
        var file = Assert.IsType<FileStreamResult>(await _sut.Download(Id, "a.txt"));
        using var reader = new StreamReader(file.FileStream);
        Assert.Equal("hello", await reader.ReadToEndAsync());
    }

    [Fact]
    public async Task Upload_WritesToTheProvider()
    {
        _sut.HttpContext.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes("new"));
        Assert.Equal(200, Status(await _sut.Upload(Id, "new.txt", CancellationToken.None)));
        Assert.Equal("new"u8.ToArray(), _h.Remote.Files["root/new.txt"]);
    }

    [Fact]
    public async Task CreateDirectory_RenameAndDelete_ReachTheProvider()
    {
        Assert.Equal(204, Status(await _sut.CreateDirectory(Id, "sub")));
        Assert.Contains("root/sub", _h.Remote.Directories);

        Assert.Equal(204, Status(await _sut.Rename(Id, new RenameRequest("a.txt", "b.txt"))));
        Assert.True(_h.Remote.Files.ContainsKey("root/b.txt"));

        Assert.Equal(204, Status(await _sut.Delete(Id, "b.txt")));
        Assert.False(_h.Remote.Files.ContainsKey("root/b.txt"));
    }

    [Fact]
    public async Task ReadGrant_RefusesChanges()
    {
        _h.Granted = CloudAccessPermission.Read;
        var sut = NewController();
        Assert.Equal(403, Status(await sut.CreateDirectory(Id, "sub")));
        Assert.Equal(403, Status(await sut.Delete(Id, "a.txt")));
        Assert.True(_h.Remote.Files.ContainsKey("root/a.txt"));
    }

    [Fact]
    public async Task Versions_AreEmpty()
        => Assert.Empty(Assert.IsAssignableFrom<System.Collections.IEnumerable>(
            Assert.IsType<OkObjectResult>(await _sut.Versions(Id, "a.txt")).Value).Cast<object>());

    // ── transfers ────────────────────────────────────────────────────

    [Fact]
    public async Task Copy_LocalToVirtual()
    {
        Assert.Equal(201, Status(await _sut.Copy(new TransferRequest(HostId, "Team/local.txt", Id, "copy.txt", false))));
        Assert.Equal("local"u8.ToArray(), _h.Remote.Files["root/copy.txt"]);
        Assert.True(File.Exists(Local("Team/local.txt")));
    }

    [Fact]
    public async Task Copy_VirtualToLocal()
    {
        Assert.Equal(201, Status(await _sut.Copy(new TransferRequest(Id, "a.txt", HostId, "Team/copy.txt", false))));
        Assert.Equal("hello", File.ReadAllText(Local("Team/copy.txt")));
    }

    [Fact]
    public async Task Move_LocalToVirtual_RemovesTheSource()
    {
        Assert.Equal(201, Status(await _sut.Move(new TransferRequest(HostId, "Team/local.txt", Id, "moved.txt", false))));
        Assert.True(_h.Remote.Files.ContainsKey("root/moved.txt"));
        Assert.False(File.Exists(Local("Team/local.txt")));
    }

    [Fact]
    public async Task Move_VirtualToLocal_IsRefused_AndKeepsTheSource()
    {
        var result = await _sut.Move(new TransferRequest(Id, "a.txt", HostId, "Team/a.txt", false));
        Assert.Equal(403, Status(result));
        Assert.Equal("move_not_supported", Code(result));
        Assert.True(_h.Remote.Files.ContainsKey("root/a.txt"));
    }

    [Fact]
    public async Task Move_WithinTheVirtualShare_Renames()
    {
        Assert.Equal(201, Status(await _sut.Move(new TransferRequest(Id, "a.txt", Id, "docs/a.txt", false))));
        Assert.True(_h.Remote.Files.ContainsKey("root/docs/a.txt"));
        Assert.False(_h.Remote.Files.ContainsKey("root/a.txt"));
    }

    // ── search ───────────────────────────────────────────────────────

    private (SearchApiController Sut, Mock<ISearchService> Search) NewSearchController()
    {
        var shares = new Mock<IShareRepository>();
        shares.Setup(r => r.GetAllEnabledAsync()).ReturnsAsync([_h.Host]);
        var search = new Mock<ISearchService>();
        search.Setup(s => s.SearchAsync(It.IsAny<string>(), _h.User, It.IsAny<string?>(), It.IsAny<string?>(),
                It.IsAny<CancellationToken>(), It.IsAny<bool>()))
            .ReturnsAsync([]);
        var sut = new SearchApiController(NewUsers().Object, shares.Object, search.Object, _h.NewRouter())
        {
            ControllerContext = new ControllerContext { HttpContext = NewContext() }
        };
        return (sut, search);
    }

    [Fact]
    public async Task Search_ByShareId_WalksNames()
    {
        var (sut, search) = NewSearchController();

        var ok = Assert.IsType<OkObjectResult>(await sut.Search(new SearchRequestDto("REPORT", Id, null)));

        var hit = Assert.Single(Assert.IsType<List<SearchHitDto>>(ok.Value));
        Assert.Equal(Id, hit.ShareId);
        Assert.Equal("Archiv", hit.ShareName);
        Assert.Equal("docs/report.txt", hit.Path);
        search.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task Search_WithoutGrant_HasNoHits()
    {
        _h.Granted = null;
        var (sut, _) = NewSearchController();
        var ok = Assert.IsType<OkObjectResult>(await sut.Search(new SearchRequestDto("REPORT", Id, null)));
        Assert.Empty(Assert.IsAssignableFrom<IEnumerable<SearchHitDto>>(ok.Value));
    }
}

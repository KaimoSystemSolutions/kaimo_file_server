using System.Security.Claims;
using System.Text;
using Kaimo_File_Server.Core.Domain;
using Kaimo_File_Server.Core.Repositories.Identity;
using Kaimo_File_Server.Core.Repositories.Shares;
using Kaimo_File_Server.Web.Controllers.WebDav;
using Kaimo_File_Server.Web.Tests.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using Xunit;

namespace Kaimo_File_Server.Web.Tests.Controllers.WebDav;

/// <summary>WebDAV over a standalone virtual share, addressed as /dav/Archiv next to a local share.</summary>
public sealed class WebDavVirtualShareTests : IDisposable
{
    private readonly MountTestHarness _h = new();
    private readonly Mock<IShareRepository> _shares = new();
    private readonly WebDavLockManager _locks = new(TimeProvider.System);
    private WebDavController _sut;

    public WebDavVirtualShareTests()
    {
        _h.Remote.Directories.Add("root/docs");
        _h.Remote.Files["root/a.txt"] = "hello"u8.ToArray();
        File.WriteAllText(Path.Combine(_h.HostPath, "Team", "local.txt"), "local");
        _shares.Setup(r => r.GetByNameAsync("projekte")).ReturnsAsync(_h.Host);
        _shares.Setup(r => r.GetAllEnabledAsync()).ReturnsAsync([_h.Host]);
        _sut = NewController();
    }

    public void Dispose() => _h.Dispose();

    private WebDavController NewController()
    {
        var users = new Mock<IUserContextFactory>();
        users.Setup(f => f.CreateByUserIdAsync(_h.User.User.Id)).ReturnsAsync(_h.User);
        var sut = new WebDavController(users.Object, _shares.Object, _h.Factory.Object,
            _locks, null!, _h.NewRouter())
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext
                {
                    RequestServices = new ServiceCollection()
                        .AddSingleton(TestGates.AllGloballyOn()).BuildServiceProvider(),
                    User = new ClaimsPrincipal(new ClaimsIdentity(
                        [new Claim(ClaimTypes.NameIdentifier, _h.User.User.Id.ToString())], "test"))
                }
            }
        };
        sut.HttpContext.Request.Host = new HostString("localhost");
        return sut;
    }

    private void At(string path, string? body = null)
    {
        _sut.HttpContext.Request.Path = path;
        _sut.HttpContext.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes(body ?? string.Empty));
    }

    private void Destination(string path)
        => _sut.HttpContext.Request.Headers["Destination"] = "http://localhost" + path;

    private static int Status(IActionResult r) => r switch
    {
        ContentResult c => c.StatusCode ?? 200,
        StatusCodeResult s => s.StatusCode,
        _ => 200
    };

    private async Task<string> PropFindAsync(string path, string depth = "1")
    {
        At(path);
        _sut.HttpContext.Request.Headers["Depth"] = depth;
        var result = Assert.IsType<ContentResult>(await _sut.PropFind());
        Assert.Equal(207, result.StatusCode);
        return result.Content!;
    }

    [Fact]
    public async Task Root_ListsTheVirtualShare_NextToLocalShares()
    {
        var xml = await PropFindAsync("/dav/");
        Assert.Contains("/dav/Archiv/", xml);
        Assert.Contains("/dav/projekte/", xml);
    }

    [Fact]
    public async Task Root_LeavesOutMountedShares()
        => Assert.DoesNotContain("/dav/Cloud/", await PropFindAsync("/dav/"));

    [Fact]
    public async Task Root_WithoutGrant_LeavesItOut()
    {
        _h.Granted = null;
        _sut = NewController();
        Assert.DoesNotContain("/dav/Archiv/", await PropFindAsync("/dav/"));
    }

    [Fact]
    public async Task Root_ConnectionNotReady_LeavesItOut()
    {
        _h.Connection.State = StorageConnectionState.Disabled;
        _sut = NewController();
        Assert.DoesNotContain("/dav/Archiv/", await PropFindAsync("/dav/"));
    }

    [Fact]
    public async Task LocalShareOfTheSameName_Wins()
    {
        var local = new ShareDefinition("Archiv", _h.HostPath);
        _shares.Setup(r => r.GetByNameAsync("Archiv")).ReturnsAsync(local);
        _h.Repository.Setup(r => r.GetMountedSharesAsync(local.Id, It.IsAny<CancellationToken>())).ReturnsAsync([]);
        _sut = NewController();

        var root = await PropFindAsync("/dav/");
        Assert.DoesNotContain("/dav/Archiv/", root); // the fake local share is not in GetAllEnabledAsync

        _sut = NewController();
        var listing = await PropFindAsync("/dav/Archiv");
        Assert.Contains("/dav/Archiv/Team/", listing);
        Assert.DoesNotContain("/dav/Archiv/a.txt", listing);
    }

    [Fact]
    public async Task PropFind_InTheShare_ListsRemoteItems()
    {
        var xml = await PropFindAsync("/dav/Archiv");
        Assert.Contains("/dav/Archiv/a.txt", xml);
        Assert.Contains("/dav/Archiv/docs/", xml);
        Assert.Contains("Archiv", xml);
    }

    [Fact]
    public async Task PropFind_WithoutGrant_IsNotFound()
    {
        _h.Granted = null;
        _sut = NewController();
        At("/dav/Archiv");
        _sut.HttpContext.Request.Headers["Depth"] = "1";
        Assert.Equal(404, Status(await _sut.PropFind()));
    }

    [Fact]
    public async Task MountedShare_IsNotAddressableByName()
    {
        At("/dav/Cloud");
        _sut.HttpContext.Request.Headers["Depth"] = "1";
        Assert.Equal(404, Status(await _sut.PropFind()));
    }

    [Fact]
    public async Task Get_ReadsTheRemoteFile()
    {
        At("/dav/Archiv/a.txt");
        var file = Assert.IsType<FileStreamResult>(await _sut.Get());
        using var reader = new StreamReader(file.FileStream);
        Assert.Equal("hello", await reader.ReadToEndAsync());
    }

    [Fact]
    public async Task Put_MkCol_Delete_ReachTheProvider()
    {
        At("/dav/Archiv/new.txt", "one");
        Assert.Equal(201, Status(await _sut.Put(CancellationToken.None)));
        Assert.Equal("one"u8.ToArray(), _h.Remote.Files["root/new.txt"]);

        _sut = NewController();
        At("/dav/Archiv/sub");
        Assert.Equal(201, Status(await _sut.MkCol()));
        Assert.Contains("root/sub", _h.Remote.Directories);

        _sut = NewController();
        At("/dav/Archiv/new.txt");
        Assert.Equal(204, Status(await _sut.Delete()));
        Assert.False(_h.Remote.Files.ContainsKey("root/new.txt"));
    }

    [Fact]
    public async Task Delete_OfTheShareRoot_IsForbidden()
    {
        At("/dav/Archiv");
        Assert.Equal(403, Status(await _sut.Delete()));
    }

    [Fact]
    public async Task Put_WithReadGrant_IsForbidden()
    {
        _h.Granted = CloudAccessPermission.Read;
        _sut = NewController();
        At("/dav/Archiv/new.txt", "one");
        Assert.Equal(403, Status(await _sut.Put(CancellationToken.None)));
        Assert.False(_h.Remote.Files.ContainsKey("root/new.txt"));
    }

    [Fact]
    public async Task Move_WithinTheShare_Renames()
    {
        At("/dav/Archiv/a.txt");
        Destination("/dav/Archiv/docs/a.txt");
        Assert.Equal(201, Status(await _sut.Move()));
        Assert.True(_h.Remote.Files.ContainsKey("root/docs/a.txt"));
    }

    [Fact]
    public async Task Move_OutOfTheShare_IsForbidden_AndKeepsTheSource()
    {
        At("/dav/Archiv/a.txt");
        Destination("/dav/projekte/Team/a.txt");
        Assert.Equal(403, Status(await _sut.Move()));
        Assert.True(_h.Remote.Files.ContainsKey("root/a.txt"));
    }

    [Fact]
    public async Task Copy_FromALocalShare_ReachesTheProvider()
    {
        At("/dav/projekte/Team/local.txt");
        Destination("/dav/Archiv/copy.txt");
        Assert.Equal(201, Status(await _sut.Copy()));
        Assert.Equal("local"u8.ToArray(), _h.Remote.Files["root/copy.txt"]);
    }

    [Fact]
    public async Task ALock_BlocksAnotherWriter()
    {
        Assert.NotNull(_locks.TryAcquire(_h.Standalone.Id, "a.txt", false, null, 60));

        At("/dav/Archiv/a.txt", "other");
        Assert.Equal(423, Status(await _sut.Put(CancellationToken.None)));
    }
}

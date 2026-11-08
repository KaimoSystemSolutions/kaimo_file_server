using System.Security.Claims;
using System.Text;
using Kaimo_File_Server.Core.Repositories;
using Kaimo_File_Server.Core.Services;
using Kaimo_File_Server.Web.Controllers.WebDav;
using Kaimo_File_Server.Web.Tests.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using Xunit;

namespace Kaimo_File_Server.Web.Tests.Controllers.WebDav;

/// <summary>WebDAV over a virtual share mounted at Team/Cloud of a real local share.</summary>
public sealed class WebDavControllerMountTests : IDisposable
{
    private readonly MountTestHarness _h = new();
    private WebDavController _sut;

    public WebDavControllerMountTests()
    {
        _h.Remote.Directories.Add("root/docs");
        _h.Remote.Files["root/a.txt"] = "hello"u8.ToArray();
        File.WriteAllText(Path.Combine(_h.HostPath, "Team", "local.txt"), "local");
        _sut = NewController();
    }

    public void Dispose() => _h.Dispose();

    private WebDavController NewController()
    {
        var users = new Mock<IUserContextFactory>();
        users.Setup(f => f.CreateByUserIdAsync(_h.User.User.Id)).ReturnsAsync(_h.User);
        var shares = new Mock<IShareRepository>();
        shares.Setup(r => r.GetByNameAsync("projekte")).ReturnsAsync(_h.Host);
        var sut = new WebDavController(users.Object, shares.Object, _h.Factory.Object,
            new WebDavLockManager(TimeProvider.System), null!, _h.NewRouter())
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

    private string Local(string rel) => Path.Combine(_h.HostPath, rel.Replace('/', Path.DirectorySeparatorChar));

    private void At(string path, string? body = null)
    {
        _sut.HttpContext.Request.Path = "/dav/projekte/" + path;
        _sut.HttpContext.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes(body ?? string.Empty));
    }

    private void Destination(string path)
        => _sut.HttpContext.Request.Headers["Destination"] = "http://localhost/dav/projekte/" + path;

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
    public async Task PropFind_OfTheParent_ListsTheMount()
        => Assert.Contains("/dav/projekte/Team/Cloud/", await PropFindAsync("Team"));

    [Fact]
    public async Task PropFind_Depth0_OfTheParent_HasNoChildren()
        => Assert.DoesNotContain("/dav/projekte/Team/Cloud/", await PropFindAsync("Team", "0"));

    [Fact]
    public async Task PropFind_InTheMount_ListsRemoteItemsUnderHostHrefs()
    {
        var xml = await PropFindAsync("Team/Cloud");
        Assert.Contains("/dav/projekte/Team/Cloud/a.txt", xml);
        Assert.Contains("/dav/projekte/Team/Cloud/docs/", xml);
    }

    [Fact]
    public async Task WithoutGrant_NoEntry_AndMkColStaysForbidden()
    {
        _h.Granted = null;
        _sut = NewController();
        Assert.DoesNotContain("Team/Cloud", await PropFindAsync("Team"));

        _sut = NewController();
        At("Team/Cloud");
        Assert.Equal(403, Status(await _sut.MkCol()));
    }

    [Fact]
    public async Task Get_ReadsTheRemoteFile()
    {
        At("Team/Cloud/a.txt");
        var file = Assert.IsType<FileStreamResult>(await _sut.Get());
        using var reader = new StreamReader(file.FileStream);
        Assert.Equal("hello", await reader.ReadToEndAsync());
    }

    [Fact]
    public async Task Get_OfTheMountRoot_IsMethodNotAllowed()
    {
        At("Team/Cloud");
        Assert.Equal(405, Status(await _sut.Get()));
    }

    [Fact]
    public async Task Put_CreatesThenOverwrites()
    {
        At("Team/Cloud/new.txt", "one");
        Assert.Equal(201, Status(await _sut.Put(CancellationToken.None)));

        _sut = NewController();
        At("Team/Cloud/new.txt", "two");
        Assert.Equal(204, Status(await _sut.Put(CancellationToken.None)));
        Assert.Equal("two", Encoding.UTF8.GetString(_h.Remote.Files["root/new.txt"]));
    }

    [Fact]
    public async Task Put_IntoAMissingFolder_IsConflict()
    {
        At("Team/Cloud/missing/new.txt", "x");
        Assert.Equal(409, Status(await _sut.Put(CancellationToken.None)));
    }

    [Fact]
    public async Task Put_OntoTheMountRoot_IsForbidden()
    {
        At("Team/Cloud", "x");
        Assert.Equal(403, Status(await _sut.Put(CancellationToken.None)));
    }

    [Fact]
    public async Task MkCol_InTheMount()
    {
        At("Team/Cloud/sub");
        Assert.Equal(201, Status(await _sut.MkCol()));
        Assert.Contains("root/sub", _h.Remote.Directories);
    }

    [Fact]
    public async Task MkCol_OnTheMountRoot_IsMethodNotAllowed()
    {
        At("Team/Cloud");
        Assert.Equal(405, Status(await _sut.MkCol()));
    }

    [Fact]
    public async Task Delete_IsPermanent()
    {
        At("Team/Cloud/a.txt");
        Assert.Equal(204, Status(await _sut.Delete()));
        Assert.False(_h.Remote.Files.ContainsKey("root/a.txt"));
    }

    [Fact]
    public async Task Delete_OfTheMountRoot_IsForbidden()
    {
        At("Team/Cloud");
        Assert.Equal(403, Status(await _sut.Delete()));
    }

    [Fact]
    public async Task Move_InsideTheMount()
    {
        At("Team/Cloud/a.txt");
        Destination("Team/Cloud/docs/a.txt");
        Assert.Equal(201, Status(await _sut.Move()));
        Assert.True(_h.Remote.Files.ContainsKey("root/docs/a.txt"));
    }

    [Fact]
    public async Task Move_LocalIntoTheMount_CopiesThenDeletes()
    {
        At("Team/local.txt");
        Destination("Team/Cloud/moved.txt");
        Assert.Equal(201, Status(await _sut.Move()));
        Assert.True(_h.Remote.Files.ContainsKey("root/moved.txt"));
        Assert.False(File.Exists(Local("Team/local.txt")));
    }

    [Fact]
    public async Task Move_RemoteToLocal_IsForbidden_BeforeTheOverwrittenTargetIsDeleted()
    {
        At("Team/Cloud/a.txt");
        Destination("Team/local.txt");
        Assert.Equal(403, Status(await _sut.Move()));
        Assert.True(File.Exists(Local("Team/local.txt")));
        Assert.True(_h.Remote.Files.ContainsKey("root/a.txt"));
    }

    [Fact]
    public async Task Copy_LocalToRemote()
    {
        At("Team/local.txt");
        Destination("Team/Cloud/copy.txt");
        Assert.Equal(201, Status(await _sut.Copy()));
        Assert.Equal("local", Encoding.UTF8.GetString(_h.Remote.Files["root/copy.txt"]));
    }

    [Fact]
    public async Task Copy_RemoteToLocal()
    {
        At("Team/Cloud/a.txt");
        Destination("Team/copy.txt");
        Assert.Equal(201, Status(await _sut.Copy()));
        Assert.Equal("hello", File.ReadAllText(Local("Team/copy.txt")));
    }

    [Fact]
    public async Task PropPatch_StampsTheModifiedTime()
    {
        At("Team/Cloud/a.txt", """
            <?xml version="1.0" encoding="utf-8"?>
            <d:propertyupdate xmlns:d="DAV:" xmlns:z="urn:schemas-microsoft-com:">
              <d:set><d:prop><z:Win32LastModifiedTime>Mon, 03 Aug 2026 12:00:00 GMT</z:Win32LastModifiedTime></d:prop></d:set>
            </d:propertyupdate>
            """);
        Assert.Equal(207, Status(await _sut.PropPatch()));
        var stamp = Assert.Single(_h.Remote.Stamped);
        Assert.Equal("root/a.txt", stamp.Path);
        Assert.Equal(new DateTime(2026, 8, 3, 12, 0, 0, DateTimeKind.Utc), stamp.Modified);
    }

    // ── a delete in the mount is permanent ───────────────────────────

    [Fact]
    public async Task Copy_OverAnExistingRemoteFile_FailingWrite_KeepsTheOldFile()
    {
        _h.Remote.WriteFailure = new IOException("connection lost");
        At("Team/local.txt");
        Destination("Team/Cloud/a.txt");

        Assert.Equal(409, Status(await _sut.Copy()));
        Assert.Equal("hello", Encoding.UTF8.GetString(_h.Remote.Files["root/a.txt"]));
    }

    [Fact]
    public async Task Move_OverAnExistingRemoteFile_FailingWrite_KeepsBothFiles()
    {
        _h.Remote.WriteFailure = new IOException("connection lost");
        At("Team/local.txt");
        Destination("Team/Cloud/a.txt");

        Assert.Equal(409, Status(await _sut.Move()));
        Assert.Equal("hello", Encoding.UTF8.GetString(_h.Remote.Files["root/a.txt"]));
        Assert.True(File.Exists(Local("Team/local.txt")));
    }

    [Fact]
    public async Task Move_OverAnExistingRemoteFile_ReplacesIt()
    {
        At("Team/local.txt");
        Destination("Team/Cloud/a.txt");

        Assert.Equal(204, Status(await _sut.Move()));
        Assert.Equal("local", Encoding.UTF8.GetString(_h.Remote.Files["root/a.txt"]));
        Assert.False(File.Exists(Local("Team/local.txt")));
    }

    [Fact]
    public async Task Move_InsideTheMount_OverAnExistingFile_ReplacesIt()
    {
        _h.Remote.Files["root/b.txt"] = "bbb"u8.ToArray();
        At("Team/Cloud/b.txt");
        Destination("Team/Cloud/a.txt");

        Assert.Equal(204, Status(await _sut.Move()));
        Assert.Equal("bbb", Encoding.UTF8.GetString(_h.Remote.Files["root/a.txt"]));
        Assert.False(_h.Remote.Files.ContainsKey("root/b.txt"));
    }

    [Fact]
    public async Task Copy_FileOverARemoteFolder_IsConflict_AndKeepsTheFolder()
    {
        _h.Remote.Files["root/docs/keep.txt"] = "k"u8.ToArray();
        At("Team/local.txt");
        Destination("Team/Cloud/docs");

        Assert.Equal(409, Status(await _sut.Copy()));
        Assert.True(_h.Remote.Files.ContainsKey("root/docs/keep.txt"));
    }

    [Fact]
    public async Task Copy_MissingSource_LeavesTheRemoteTargetAlone()
    {
        At("Team/gone.txt");
        Destination("Team/Cloud/a.txt");

        Assert.Equal(404, Status(await _sut.Copy()));
        Assert.True(_h.Remote.Files.ContainsKey("root/a.txt"));
    }

    [Theory]
    [InlineData("Team/Cloud/docs/b.txt", "Team/Cloud/docs")]
    [InlineData("Team/Cloud/docs", "Team/Cloud/docs/sub")]
    [InlineData("Team/Cloud/docs/b.txt", "Team/Cloud/docs/b.txt")]
    public async Task Move_OntoItsOwnAncestorOrDescendant_IsForbidden_AndDeletesNothing(string from, string to)
    {
        _h.Remote.Files["root/docs/b.txt"] = "b"u8.ToArray();
        At(from);
        Destination(to);

        Assert.Equal(403, Status(await _sut.Move()));
        Assert.True(_h.Remote.Files.ContainsKey("root/docs/b.txt"));
    }

    [Fact]
    public async Task Move_OfAFolderIntoItsOwnMount_IsForbidden_AndCopiesNothing()
    {
        At("Team");
        Destination("Team/Cloud/x");

        Assert.Equal(403, Status(await _sut.Move()));
        Assert.True(File.Exists(Local("Team/local.txt")));
        Assert.DoesNotContain("root/x", _h.Remote.Directories);
    }

    // ── what a request costs at the provider ─────────────────────────

    [Fact]
    public async Task Head_DoesNotOpenTheRemoteFile_AndReportsItsLength()
    {
        At("Team/Cloud/a.txt");
        _sut.HttpContext.Request.Method = "HEAD";

        Assert.Equal(200, Status(await _sut.Get()));
        Assert.Equal(0, _h.Remote.ReadCalls);
        Assert.Equal(5, _sut.Response.ContentLength);
    }

    [Fact]
    public async Task Delete_SeesAFileCreatedOutsideKaimo_DespiteACachedListing()
    {
        await PropFindAsync("Team/Cloud");
        _h.Remote.Files["root/external.txt"] = [];

        _sut = NewController();
        At("Team/Cloud/external.txt");
        _sut.HttpContext.Request.Method = "DELETE";

        Assert.Equal(204, Status(await _sut.Delete()));
        Assert.False(_h.Remote.Files.ContainsKey("root/external.txt"));
    }

    [Fact]
    public async Task Get_KeepsUsingTheListingCache()
    {
        await PropFindAsync("Team/Cloud");
        var listed = _h.Remote.ListCalls["root"];

        _sut = NewController();
        At("Team/Cloud/a.txt");
        _sut.HttpContext.Request.Method = "GET";
        await _sut.Get();

        Assert.Equal(listed, _h.Remote.ListCalls["root"]);
    }

    [Fact]
    public async Task ProviderOutage_Answers503()
    {
        _h.Remote.ListFailure = _ => new HttpRequestException("boom");
        At("Team/Cloud");
        _sut.HttpContext.Request.Headers["Depth"] = "1";
        // The mount root itself is synthetic, so the failure surfaces when its children are listed.
        Assert.Equal(503, Status(await _sut.PropFind()));
    }
}

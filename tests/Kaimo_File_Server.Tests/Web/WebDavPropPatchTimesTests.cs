using System.Security.Claims;
using System.Text;
using Kaimo_File_Server.Core.Domain;
using Kaimo_File_Server.Core.Domain.Identity;
using Kaimo_File_Server.Core.Repositories;
using Kaimo_File_Server.Core.Security;
using Kaimo_File_Server.Core.Services;
using Kaimo_File_Server.Core.Services.File;
using Kaimo_File_Server.Infrastructure.Storage;
using Kaimo_File_Server.Web.Controllers.WebDav;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using Xunit;

namespace Kaimo_File_Server.Tests;

/// <summary>
/// PROPPATCH keeps the Win32 times Windows Explorer and Kaimo's own WebDAV sync send, so a
/// copy onto Kaimo over WebDAV keeps its modification and creation time.
/// </summary>
public sealed class WebDavPropPatchTimesTests : IDisposable
{
    private static readonly DateTime Modified = new(2026, 8, 3, 12, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime Created = new(2024, 1, 2, 3, 4, 5, DateTimeKind.Utc);

    private readonly string _root = Directory.CreateTempSubdirectory("kaimo-dav-times-").FullName;
    private readonly WebDavController _sut;
    private readonly FileSystemStorage _storage;
    private readonly Mock<IAclService> _acl = new();

    public WebDavPropPatchTimesTests()
    {
        var share = new ShareDefinition("a", _root, isEnabled: true);
        var user = new UserContext(new User(Guid.NewGuid(), "Test", "test", "hash", "nt"), [], [], []);
        _storage = new FileSystemStorage(share.Path, share.Id, null);

        var acl = _acl;
        acl.Setup(a => a.HasAccessAsync(It.IsAny<UserContext>(), It.IsAny<Guid>(), It.IsAny<string>(),
                It.IsAny<bool>(), It.IsAny<FilePermission>()))
            .ReturnsAsync(true);
        var users = new Mock<IUserContextFactory>();
        users.Setup(f => f.CreateByUserIdAsync(user.User.Id)).ReturnsAsync(user);
        var shares = new Mock<IShareRepository>();
        shares.Setup(r => r.GetByNameAsync("a")).ReturnsAsync(share);
        var factory = new Mock<IFileServiceFactory>();
        factory.Setup(f => f.CreateForShare(It.IsAny<ShareDefinition>()))
            .Returns<ShareDefinition>(s => new FileService(
                new FileSystemStorage(s.Path, s.Id, null), acl.Object, null, s.Id));

        _sut = new WebDavController(users.Object, shares.Object, factory.Object,
            new WebDavLockManager(TimeProvider.System), null!)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext
                {
                    RequestServices = new ServiceCollection()
                        .AddSingleton(TestGates.AllGloballyOn()).BuildServiceProvider(),
                    User = new ClaimsPrincipal(new ClaimsIdentity(
                        [new Claim(ClaimTypes.NameIdentifier, user.User.Id.ToString())], "test"))
                }
            }
        };
        _sut.HttpContext.Request.Host = new HostString("localhost");
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
    }

    private Task<IActionResult> PropPatchAsync(string path, string props)
    {
        _sut.HttpContext.Request.Path = path;
        _sut.HttpContext.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes($"""
            <?xml version="1.0" encoding="utf-8"?>
            <d:propertyupdate xmlns:d="DAV:" xmlns:z="urn:schemas-microsoft-com:">
              <d:set><d:prop>{props}</d:prop></d:set>
            </d:propertyupdate>
            """));
        return _sut.PropPatch();
    }

    [Fact]
    public async Task PropPatch_Win32CreationTime_SetsTheCreationTime()
    {
        File.WriteAllText(Path.Combine(_root, "a.txt"), "x");

        await PropPatchAsync("/dav/a/a.txt", $"<z:Win32CreationTime>{Created:R}</z:Win32CreationTime>");

        Assert.Equal(Created, (await _storage.GetMetadataAsync("a.txt")).CreatedAt);
    }

    [Fact]
    public async Task PropPatch_BothWin32Times_SetsModifiedAndCreationTime()
    {
        Directory.CreateDirectory(Path.Combine(_root, "dir"));

        await PropPatchAsync("/dav/a/dir/",
            $"<z:Win32CreationTime>{Created:R}</z:Win32CreationTime>" +
            $"<z:Win32LastModifiedTime>{Modified:R}</z:Win32LastModifiedTime>");

        var meta = await _storage.GetMetadataAsync("dir");
        Assert.Equal(Created, meta.CreatedAt);
        Assert.Equal(Modified, meta.ModifiedAt);
    }

    [Fact]
    public async Task PropPatch_EchoedCreationTime_LeavesTheStoredCreationTimeUntouched()
    {
        // The Windows redirector echoes the creation time it read from PROPFIND (whole seconds) in
        // routine PROPPATCHes. That is no change: no write, so no sub-second loss and no feed event.
        string file = Path.Combine(_root, "a.txt");
        File.WriteAllText(file, "x");
        var precise = Created.AddMilliseconds(789);
        Assert.True(FileBirthTime.TrySetUtc(new FileInfo(file), precise));

        await PropPatchAsync("/dav/a/a.txt", $"<z:Win32CreationTime>{Created:R}</z:Win32CreationTime>");

        Assert.Equal(precise, (await _storage.GetMetadataAsync("a.txt")).CreatedAt);
    }

    [Fact]
    public async Task PropPatch_EchoedCreationTimeWithNewModifiedTime_SetsOnlyTheModifiedTime()
    {
        string file = Path.Combine(_root, "a.txt");
        File.WriteAllText(file, "x");
        var precise = Created.AddMilliseconds(789);
        Assert.True(FileBirthTime.TrySetUtc(new FileInfo(file), precise));

        await PropPatchAsync("/dav/a/a.txt",
            $"<z:Win32CreationTime>{Created:R}</z:Win32CreationTime>" +
            $"<z:Win32LastModifiedTime>{Modified:R}</z:Win32LastModifiedTime>");

        var meta = await _storage.GetMetadataAsync("a.txt");
        Assert.Equal(Modified, meta.ModifiedAt);
        Assert.Equal(precise, meta.CreatedAt);
    }

    [Fact]
    public async Task PropPatch_LaterCreationTime_KeepsTheStoredCreationTime()
    {
        // A stale echo: Explorer listed the folder before a sync moved the creation time back, then
        // sends the old value in a routine PROPPATCH. An earlier creation time is always the better one.
        string file = Path.Combine(_root, "a.txt");
        File.WriteAllText(file, "x");
        Assert.True(FileBirthTime.TrySetUtc(new FileInfo(file), Created));

        await PropPatchAsync("/dav/a/a.txt",
            $"<z:Win32CreationTime>{Created.AddDays(30):R}</z:Win32CreationTime>");

        Assert.Equal(Created, (await _storage.GetMetadataAsync("a.txt")).CreatedAt);
    }

    [Fact]
    public async Task PropPatch_LaterCreationTime_WithoutWritePermission_StillSucceeds()
    {
        // Ignoring it means no write, so a read-only user's routine PROPPATCH keeps its 207
        // instead of turning into a 403.
        string file = Path.Combine(_root, "a.txt");
        File.WriteAllText(file, "x");
        Assert.True(FileBirthTime.TrySetUtc(new FileInfo(file), Created));
        _acl.Setup(a => a.HasAccessAsync(It.IsAny<UserContext>(), It.IsAny<Guid>(), It.IsAny<string>(),
                It.IsAny<bool>(), It.Is<FilePermission>(p => (p & FilePermission.WriteAll) != 0)))
            .ReturnsAsync(false);

        var result = await PropPatchAsync("/dav/a/a.txt",
            $"<z:Win32CreationTime>{Created.AddDays(30):R}</z:Win32CreationTime>");

        Assert.Equal(StatusCodes.Status207MultiStatus, Assert.IsType<ContentResult>(result).StatusCode);
    }

    [Fact]
    public async Task PropPatch_OnlyModifiedTime_KeepsTheCreationTime()
    {
        File.WriteAllText(Path.Combine(_root, "a.txt"), "x");
        var before = (await _storage.GetMetadataAsync("a.txt")).CreatedAt;

        await PropPatchAsync("/dav/a/a.txt", $"<z:Win32LastModifiedTime>{Modified:R}</z:Win32LastModifiedTime>");

        var meta = await _storage.GetMetadataAsync("a.txt");
        Assert.Equal(Modified, meta.ModifiedAt);
        Assert.Equal(before, meta.CreatedAt);
    }
}

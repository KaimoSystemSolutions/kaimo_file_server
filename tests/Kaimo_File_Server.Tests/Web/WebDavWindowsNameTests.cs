using System.Security.Claims;
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
/// WebDAV clients read a resource back at the URL they wrote, so a name Windows cannot use is
/// refused (400) instead of being renamed behind the client's back.
/// </summary>
public sealed class WebDavWindowsNameTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("kaimo-dav-names-").FullName;
    private readonly ShareDefinition _share;
    private readonly WebDavController _sut;
    private readonly UserContext _user;

    public WebDavWindowsNameTests()
    {
        _share = new ShareDefinition("a", _root, isEnabled: true);
        var user = _user = new UserContext(new User(Guid.NewGuid(), "Test", "test", "hash", "nt"), [], [], []);

        var acl = new Mock<IAclService>();
        acl.Setup(a => a.HasAccessAsync(It.IsAny<UserContext>(), It.IsAny<Guid>(), It.IsAny<string>(),
                It.IsAny<bool>(), It.IsAny<FilePermission>()))
            .ReturnsAsync(true);

        var users = new Mock<IUserContextFactory>();
        users.Setup(f => f.CreateByUserIdAsync(user.User.Id)).ReturnsAsync(user);
        var shares = new Mock<IShareRepository>();
        shares.Setup(r => r.GetByNameAsync("a")).ReturnsAsync(_share);
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

    private void Request(string path, string? destination = null)
    {
        _sut.HttpContext.Request.Path = path;
        _sut.HttpContext.Request.Body = new MemoryStream([1, 2, 3]);
        if (destination is not null)
            _sut.HttpContext.Request.Headers["Destination"] = destination;
    }

    private static int Status(IActionResult r) => Assert.IsAssignableFrom<StatusCodeResult>(r).StatusCode;

    [Theory]
    [InlineData("/dav/a/x%3F.txt")]
    [InlineData("/dav/a/CON.txt")]
    [InlineData("/dav/a/trailing.")]
    public async Task Put_with_invalid_name_is_rejected(string path)
    {
        Request(Uri.UnescapeDataString(path));

        Assert.Equal(400, Status(await _sut.Put(CancellationToken.None)));
        Assert.Empty(Directory.EnumerateFileSystemEntries(_root));
    }

    [Fact]
    public async Task Put_with_webdav_switched_off_for_the_user_is_refused()
    {
        _user.User.ServiceAccess = UserServiceAccess.All & ~UserServiceAccess.WebDav;
        Request("/dav/a/ok.txt");

        Assert.IsType<ChallengeResult>(await _sut.Put(CancellationToken.None));
        Assert.Empty(Directory.EnumerateFileSystemEntries(_root));
    }

    [Fact]
    public async Task Put_with_valid_name_still_works()
    {
        Request("/dav/a/ok.txt");

        Assert.Equal(201, Status(await _sut.Put(CancellationToken.None)));
        Assert.True(File.Exists(Path.Combine(_root, "ok.txt")));
    }

    [Fact]
    public async Task MkCol_with_invalid_name_is_rejected()
    {
        Request("/dav/a/x*");
        _sut.HttpContext.Request.Body = Stream.Null;

        Assert.Equal(400, Status(await _sut.MkCol()));
        Assert.Empty(Directory.EnumerateFileSystemEntries(_root));
    }

    [Fact]
    public async Task Move_to_invalid_name_is_rejected_and_keeps_source()
    {
        File.WriteAllText(Path.Combine(_root, "f.txt"), "x");
        Request("/dav/a/f.txt", "http://localhost/dav/a/f%7C.txt");

        Assert.Equal(400, Status(await _sut.Move()));
        Assert.True(File.Exists(Path.Combine(_root, "f.txt")));
    }
}

using Kaimo_File_Server.Core.Domain;
using Kaimo_File_Server.Core.Domain.Identity;
using Kaimo_File_Server.Core.Repositories;
using Kaimo_File_Server.Core.Security;
using Kaimo_File_Server.Core.Services;
using Kaimo_File_Server.Core.Services.File;
using Kaimo_File_Server.Infrastructure.Configuration;
using Kaimo_File_Server.Web.Controllers;
using Kaimo_File_Server.Web.Services;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Moq;
using Xunit;

namespace Kaimo_File_Server.Tests;

/// <summary>
/// Anonymous share-link access runs under the creator's identity, so disabling
/// the creator must stop their links — on the landing page and per download —
/// and link passwords must keep their lockout under parallel guessing.
/// </summary>
public sealed class ShareLinkCreatorTests
{
    private readonly Mock<IUserContextFactory> _contexts = new();
    private readonly Mock<IPasswordService> _passwords = new();
    private readonly LoginThrottle _throttle = new();
    private readonly ShareLink _link = new()
    {
        Token = "abc123", ShareId = Guid.NewGuid(), RootRelativePath = "docs",
        IsDirectory = true, CreatedByUserId = Guid.NewGuid(), PasswordHash = "link-hash"
    };

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ResolveCreator_ReturnsContextOnlyForEnabledCreator(bool enabled)
    {
        ArrangeCreator(enabled);

        var creator = await Service().ResolveCreatorAsync(_link);

        Assert.Equal(enabled, creator is not null);
    }

    [Fact]
    public async Task ResolveCreator_DeletedCreator_ReturnsNull()
    {
        _contexts.Setup(c => c.CreateByUserIdAsync(_link.CreatedByUserId)).ReturnsAsync((UserContext?)null);
        Assert.Null(await Service().ResolveCreatorAsync(_link));
    }

    [Fact]
    public async Task PublicDownload_DisabledCreator_IsNotFound()
    {
        ArrangeCreator(enabled: false);
        var tickets = new PublicDownloadTicketStore();
        var shareLinks = new Mock<IShareLinkRepository>();
        shareLinks.Setup(r => r.TryConsumeAccessAsync(_link.Token)).ReturnsAsync(_link);
        var shares = new Mock<IShareRepository>();
        shares.Setup(r => r.GetByIdAsync(_link.ShareId)).ReturnsAsync(
            new ShareDefinition("docs", "/data/docs") { Id = _link.ShareId });
        var fileServices = new Mock<IFileServiceFactory>();
        var controller = new PublicDownloadController(
            tickets, shareLinks.Object, shares.Object, fileServices.Object, _contexts.Object)
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() }
        };
        string ticket = tickets.Issue(new PublicDownloadTicket(_link.Token, ["docs/a.txt"], "a.txt", Zip: false));

        var result = await controller.Download(ticket, CancellationToken.None);

        Assert.IsType<NotFoundResult>(result);
        fileServices.Verify(f => f.CreateForShare(It.IsAny<Guid>(), It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public void VerifyPassword_LocksAfterTenWrongGuesses_AndCorrectOneResets()
    {
        _passwords.Setup(p => p.VerifyPassword("right", "link-hash")).Returns(true);
        var service = Service();

        Assert.True(service.VerifyPassword(_link, "right").Ok);
        for (int i = 0; i < 9; i++)
            Assert.False(service.VerifyPassword(_link, "wrong").Ok);
        Assert.True(service.VerifyPassword(_link, "right").Ok);   // counter reset
        for (int i = 0; i < 10; i++)
            service.VerifyPassword(_link, "wrong");

        var locked = service.VerifyPassword(_link, "right");
        Assert.False(locked.Ok);
        Assert.True(locked.RetryAfter > TimeSpan.Zero);
    }

    [Fact]
    public void VerifyPassword_ParallelGuesses_VerifyAtMostBudget()
    {
        int verifications = 0;
        _passwords.Setup(p => p.VerifyPassword(It.IsAny<string>(), "link-hash"))
            .Returns(() => { Interlocked.Increment(ref verifications); Thread.Sleep(20); return false; });
        var service = Service();

        Parallel.For(0, 50, new ParallelOptions { MaxDegreeOfParallelism = 25 },
            _ => service.VerifyPassword(_link, "guess"));

        Assert.True(verifications <= 10, $"{verifications} hashes verified");
    }

    private void ArrangeCreator(bool enabled)
    {
        var user = new User(_link.CreatedByUserId, "Creator", "creator", "hash", "nt", isEnabled: enabled);
        _contexts.Setup(c => c.CreateByUserIdAsync(_link.CreatedByUserId))
            .ReturnsAsync(new UserContext(user, [], [], []));
    }

    private ShareLinkService Service() => new(
        Mock.Of<IShareLinkRepository>(),
        Mock.Of<IConfigRepository>(),
        _passwords.Object,
        _throttle,
        Mock.Of<IManagementAuthService>(),
        _contexts.Object,
        Mock.Of<AuthenticationStateProvider>(),
        new PublicDownloadTicketStore());
}

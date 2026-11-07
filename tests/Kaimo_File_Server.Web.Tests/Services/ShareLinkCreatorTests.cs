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

namespace Kaimo_File_Server.Web.Tests.Services;

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
        var policy = new PublicShareAccessPolicy(shareLinks.Object, shares.Object, _contexts.Object,
            ShareLinkHostRestrictionTests.Config(new ServerDomainSettings(), restrict: false), new LinkAccessNotificationGate());
        var controller = new PublicDownloadController(tickets, policy, fileServices.Object)
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() }
        };
        string ticket = tickets.Issue(new PublicDownloadTicket(_link.Token, ["docs/a.txt"], "a.txt", Zip: false));

        var result = await controller.Download(ticket, CancellationToken.None);

        Assert.IsType<NotFoundResult>(result);
        fileServices.Verify(f => f.CreateForShare(It.IsAny<ShareDefinition>()), Times.Never);
    }

    [Fact]
    public async Task PublicDownload_DownloadLinksDisabled_IsNotFound_WithoutConsumingAccess()
    {
        ArrangeCreator(enabled: true);
        var tickets = new PublicDownloadTicketStore();
        var shareLinks = new Mock<IShareLinkRepository>();
        var config = new Mock<IConfigRepository>();
        config.Setup(c => c.GetAsync(ShareLinkSettings.ConfigKey, It.IsAny<ShareLinkSettings>()))
            .ReturnsAsync(new ShareLinkSettings { RestrictToDefaultDomain = false, AllowDownloadLinks = false });
        var policy = new PublicShareAccessPolicy(shareLinks.Object, Mock.Of<IShareRepository>(), _contexts.Object,
            config.Object, new LinkAccessNotificationGate());
        var controller = new PublicDownloadController(tickets, policy, Mock.Of<IFileServiceFactory>())
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() }
        };
        string ticket = tickets.Issue(new PublicDownloadTicket(_link.Token, ["docs/a.txt"], "a.txt", Zip: false));

        Assert.IsType<NotFoundResult>(await controller.Download(ticket, CancellationToken.None));
        shareLinks.Verify(r => r.TryConsumeAccessAsync(It.IsAny<string>()), Times.Never);
    }

    // Password throttling of links is covered by ShareLinkPasswordThrottleTests.

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

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
/// Public links open only under the default server domain while the restriction is on (any host
/// when it is off or no default domain is set).
/// </summary>
public sealed class ShareLinkHostRestrictionTests
{
    [Theory]
    [InlineData("files.example.com", true)]
    [InlineData("FILES.example.com", true)]
    [InlineData("other.example.com", false)]
    [InlineData("192.168.1.10", false)]
    [InlineData(null, false)]
    public async Task OnlyDefaultDomainHostIsAllowed(string? host, bool expected)
    {
        var domains = new ServerDomainSettings
        {
            Addresses = ["https://other.example.com", "https://files.example.com:8443"],
            DefaultIndex = 1,
        };
        Assert.Equal(expected, await Service(domains).IsAllowedHostAsync(host));
    }

    [Fact]
    public async Task AnyHostIsAllowedWithoutDefaultDomain()
        => Assert.True(await Service(new ServerDomainSettings()).IsAllowedHostAsync("anything.local"));

    [Fact]
    public async Task AnyHostIsAllowedWhenRestrictionIsOff()
        => Assert.True(await Service(new ServerDomainSettings { Addresses = ["https://files.example.com"] }, restrict: false)
            .IsAllowedHostAsync("192.168.1.10"));

    [Fact]
    public async Task InternationalizedDomainMatchesPunycodeHost()
        => Assert.True(await Service(new ServerDomainSettings { Addresses = ["https://dateien.müller.de"] })
            .IsAllowedHostAsync("dateien.xn--mller-kva.de"));

    [Fact]
    public async Task DownloadUnderOtherHostIsRejected()
    {
        var links = new Mock<IShareLinkRepository>();
        var tickets = new PublicDownloadTicketStore();
        var controller = new PublicDownloadController(
            tickets, links.Object, Mock.Of<IShareRepository>(), Mock.Of<IFileServiceFactory>(),
            Mock.Of<IUserContextFactory>(), Config(new ServerDomainSettings { Addresses = ["https://files.example.com"] }))
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() }
        };
        controller.HttpContext.Request.Host = new HostString("192.168.1.10", 8443);
        var ticket = tickets.Issue(new PublicDownloadTicket("token", ["a.txt"], "a.txt", Zip: false));

        Assert.IsType<NotFoundResult>(await controller.Download(ticket, CancellationToken.None));
        links.Verify(r => r.TryConsumeAccessAsync(It.IsAny<string>()), Times.Never);
    }

    internal static IConfigRepository Config(ServerDomainSettings domains, bool restrict = true)
    {
        var config = new Mock<IConfigRepository>();
        config.Setup(c => c.GetAsync(ShareLinkSettings.ConfigKey, It.IsAny<ShareLinkSettings>()))
            .ReturnsAsync(new ShareLinkSettings { RestrictToDefaultDomain = restrict });
        config.Setup(c => c.GetAsync<ServerDomainSettings?>(ServerDomainSettings.ConfigKey, It.IsAny<ServerDomainSettings?>()))
            .ReturnsAsync(domains);
        return config.Object;
    }

    private static ShareLinkService Service(ServerDomainSettings domains, bool restrict = true)
    {
        return new ShareLinkService(
            Mock.Of<IShareLinkRepository>(), Config(domains, restrict), Mock.Of<IPasswordService>(), new LoginThrottle(),
            Mock.Of<IManagementAuthService>(), Mock.Of<IUserContextFactory>(), Mock.Of<AuthenticationStateProvider>(),
            new PublicDownloadTicketStore());
    }
}

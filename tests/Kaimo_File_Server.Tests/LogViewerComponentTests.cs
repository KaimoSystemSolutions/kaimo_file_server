using Bunit;
using Bunit.TestDoubles;
using Kaimo_File_Server.Core.Domain.Identity;
using Kaimo_File_Server.Core.Logging;
using Kaimo_File_Server.Core.Repositories;
using Kaimo_File_Server.Core.Security;
using Kaimo_File_Server.Core.Services;
using Kaimo_File_Server.Web.Components.Pages.Settings.Components;
using Kaimo_File_Server.Web.Components.ViewModels;
using Kaimo_File_Server.Web.Services;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Kaimo_File_Server.Tests;

public class LogViewerComponentTests
{
    // Renders the real LogViewer against a mocked reader that honours the level
    // allow-list, then drives the level dropdown exactly as a user would.
    [Fact]
    public void Deselecting_a_level_filters_the_visible_list_immediately()
    {
        using var ctx = new Bunit.TestContext();

        var reader = new Mock<ILogArchiveReader>();
        reader.Setup(r => r.GetSourcesAsync(It.IsAny<CancellationToken>()))
              .ReturnsAsync(new[] { "web" });
        reader.Setup(r => r.QueryAsync(It.IsAny<LogArchiveQuery>(), It.IsAny<CancellationToken>()))
              .ReturnsAsync((LogArchiveQuery query, CancellationToken _) =>
              {
                  var all = new List<LogArchiveEntry>
                  {
                      new() { Sequence = 2, Level = LogLevel.Error, Service = "web", Message = "err-msg" },
                      new() { Sequence = 1, Level = LogLevel.Information, Service = "web", Message = "info-msg" },
                  };
                  var filtered = query.Levels is null
                      ? all
                      : all.Where(e => query.Levels.Contains(e.Level)).ToList();
                  return new LogArchiveQueryResult(filtered, false);
              });

        var authz = ctx.AddTestAuthorization();
        authz.SetAuthorized("tester");

        var userContext = new UserContext(
            new User(Guid.NewGuid(), "Tester", "tester", "hash", "hash"), [], [], []);
        var userContextFactory = new Mock<IUserContextFactory>();
        userContextFactory.Setup(f => f.CreateByUsernameAsync("tester")).ReturnsAsync(userContext);

        var managementAuth = new Mock<IManagementAuthService>();
        managementAuth
            .Setup(a => a.HasGlobalPermissionAsync(It.IsAny<UserContext>(), ManagementPermission.ViewSystemLogs))
            .ReturnsAsync(true);

        var tokens = new LogDownloadTokenService(new EphemeralDataProtectionProvider());

        ctx.Services.AddScoped(sp => new LogViewerViewModel(
            reader.Object,
            tokens,
            sp.GetRequiredService<AuthenticationStateProvider>(),
            userContextFactory.Object,
            managementAuth.Object,
            NullLogger<LogViewerViewModel>.Instance));

        var cut = ctx.RenderComponent<LogViewer>();

        // Wait for the initial (all-levels) load to render both rows.
        cut.WaitForAssertion(() => Assert.Contains("info-msg", cut.Markup), TimeSpan.FromSeconds(5));
        Assert.Contains("err-msg", cut.Markup);

        // Open the level dropdown and uncheck "Information" (the first option).
        cut.Find(".multi-select .multi-select-display").Click();
        cut.FindAll(".multi-select-dropdown .multi-select-option input")[0].Change(false);

        // The Information row must disappear immediately, Error must remain.
        cut.WaitForAssertion(() => Assert.DoesNotContain("info-msg", cut.Markup), TimeSpan.FromSeconds(5));
        Assert.Contains("err-msg", cut.Markup);
    }
}

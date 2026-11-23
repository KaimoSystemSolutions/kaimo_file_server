using System.Security.Claims;
using Bunit;
using Kaimo_File_Server.Core.Domain.Identity;
using Kaimo_File_Server.Core.Repositories.Identity;
using Kaimo_File_Server.Core.Repositories.Shares;
using Kaimo_File_Server.Core.Security.Acl;
using Kaimo_File_Server.Core.Security.Management;
using Kaimo_File_Server.Core.Services.Management;
using Kaimo_File_Server.Core.Services.Search;
using Kaimo_File_Server.Search;
using Kaimo_File_Server.Web.Components.Search;
using Kaimo_File_Server.Web.Services.Files;
using Kaimo_File_Server.Web.Services.Ui;
using Kaimo_File_Server.Web.Tests.Services;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using Xunit;

namespace Kaimo_File_Server.Web.Tests.Search;

/// <summary>
/// "Show all results" from the search bar: a search still running when the results page opens
/// must not fill the bar's dropdown afterwards (on /search it lists settings &amp; tabs only).
/// </summary>
public sealed class GlobalSearchShowAllTests
{
    private readonly UserContext _actor = new(new User(Guid.NewGuid(), "Rita", "rita", "hash", "nt"), [], [], []);

    private static FileDocument Hit(string name) => new()
    {
        Id = "docs:" + name, FileName = name, ShareName = "docs", SharePath = name,
        AbsolutePath = "/srv/docs/" + name, FileType = "txt",
    };

    [Fact]
    public async Task ShowAllResults_DropsTheSearchStillInFlight()
    {
        var search = new Mock<ISearchService>();
        search.Setup(s => s.SearchAsync("repo", It.IsAny<UserContext>(), It.IsAny<string?>(), It.IsAny<string?>(),
                It.IsAny<CancellationToken>(), It.IsAny<bool>(), It.IsAny<int?>()))
            .ReturnsAsync([Hit("repo.txt")]);
        var pending = new TaskCompletionSource<List<FileDocument>>();
        var started = new TaskCompletionSource();
        search.Setup(s => s.SearchAsync("report", It.IsAny<UserContext>(), It.IsAny<string?>(), It.IsAny<string?>(),
                It.IsAny<CancellationToken>(), It.IsAny<bool>(), It.IsAny<int?>()))
            .Callback(() => started.TrySetResult())
            .Returns(pending.Task);

        using var ctx = Context(search.Object);
        var cut = ctx.Render<GlobalSearch>();
        cut.Find("select.search-scope-select").Change("global");

        cut.Find("input.search-input").Input("repo");
        cut.WaitForAssertion(() => Assert.Contains("has-results", cut.Find(".search-bar").ClassName),
            TimeSpan.FromSeconds(3));

        // The next term is still being searched when Enter opens the results page.
        cut.Find("input.search-input").Input("report");
        await started.Task.WaitAsync(TimeSpan.FromSeconds(3));
        // KeyDown is queued while the renderer is still busy with the search: wait for it.
        cut.Find("input.search-input").KeyDown(new KeyboardEventArgs { Key = "Enter" });
        var nav = ctx.Services.GetRequiredService<NavigationManager>();
        cut.WaitForAssertion(() => Assert.EndsWith("/search", nav.Uri), TimeSpan.FromSeconds(3));

        pending.SetResult([Hit("report.txt")]);
        await Task.Delay(200);
        cut.Render();

        Assert.DoesNotContain("has-results", cut.Find(".search-bar").ClassName);
    }

    private BunitContext Context(ISearchService search)
    {
        var contexts = new Mock<IUserContextFactory>();
        contexts.Setup(c => c.CreateByUsernameAsync("rita")).ReturnsAsync(_actor);
        var auth = new Mock<AuthenticationStateProvider>();
        auth.Setup(a => a.GetAuthenticationStateAsync()).ReturnsAsync(new AuthenticationState(
            new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.Name, "rita")], "test"))));
        var mgmt = new Mock<IManagementAuthService>();
        mgmt.Setup(m => m.GetAuthorizedShareIdsAnyAsync(_actor, It.IsAny<ManagementPermission>()))
            .ReturnsAsync(AuthorizedScopeResult.LimitedTo([]));
        var shares = new Mock<IShareRepository>();
        shares.Setup(s => s.GetAllAsync()).ReturnsAsync([]);
        var env = new Mock<Microsoft.AspNetCore.Hosting.IWebHostEnvironment>();
        env.SetupGet(e => e.WebRootPath).Returns(MailNotificationTests.FindWebRoot());

        var ctx = new BunitContext();
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        ctx.Services.AddSingleton(new AssetProvider(env.Object));
        ctx.Services.AddSingleton(search);
        ctx.Services.AddSingleton(contexts.Object);
        ctx.Services.AddSingleton(mgmt.Object);
        ctx.Services.AddSingleton(auth.Object);
        ctx.Services.AddSingleton(new FileSelectionCoordinator());
        ctx.Services.AddSingleton(new SearchResultsCoordinator());
        ctx.Services.AddSingleton(shares.Object);
        ctx.Services.AddSingleton(Mock.Of<IAclService>());
        return ctx;
    }
}

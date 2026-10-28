using System.Security.Claims;
using Bunit;
using Kaimo_File_Server.Core.Domain;
using Kaimo_File_Server.Core.Domain.Identity;
using Kaimo_File_Server.Core.Repositories;
using Kaimo_File_Server.Core.Security;
using Kaimo_File_Server.Core.Services;
using Kaimo_File_Server.Search;
using Kaimo_File_Server.Web.Components.Search;
using Kaimo_File_Server.Web.Components.Shared;
using Kaimo_File_Server.Web.Services;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using Xunit;

namespace Kaimo_File_Server.Tests;

/// <summary>
/// F22: search gates agree with the page and server gates — the /backup destination is
/// reachable with restore-only rights, and the share picker offers a share to a scoped manager
/// only when they could read it themselves (management scope alone is not enough).
/// </summary>
public sealed class SearchScopeGateTests
{
    private readonly UserContext _actor = new(new User(Guid.NewGuid(), "Rita", "rita", "hash", "nt"), [], [], []);
    private readonly Mock<IManagementAuthService> _mgmt = new();

    [Fact]
    public async Task RestoreOnlyRole_FindsTheBackupPage()
    {
        _mgmt.Setup(m => m.HasAnyPermissionAsync(_actor, It.IsAny<ManagementPermission>()))
            .ReturnsAsync((UserContext _, ManagementPermission anyOf) => (anyOf & ManagementPermission.RestoreFromBackup) != 0);

        var found = await SearchDestinations.MatchAsync("restore", _mgmt.Object, _actor);

        Assert.Contains(found, d => d.Url == "/backup");
    }

    [Theory]
    [InlineData("password", "/settings?tab=passwordpolicy")]
    [InlineData("benachrichtigungen", "/notifications")]
    public async Task GlobalOnlyPages_AreFoundOnlyWithTheBitAtGlobalScope(string query, string url)
    {
        // The bits are held at a department/share only: HasAny is true, the global mask is empty.
        _mgmt.Setup(m => m.HasAnyPermissionAsync(_actor, It.IsAny<ManagementPermission>())).ReturnsAsync(true);
        _mgmt.Setup(m => m.GetEffectivePermissionsAtAsync(_actor, ScopeType.Global, Guid.Empty))
            .ReturnsAsync(ManagementPermission.None);
        Assert.DoesNotContain(await SearchDestinations.MatchAsync(query, _mgmt.Object, _actor), d => d.Url == url);

        _mgmt.Setup(m => m.GetEffectivePermissionsAtAsync(_actor, ScopeType.Global, Guid.Empty))
            .ReturnsAsync(ManagementPermission.ManageSystemSettings | ManagementPermission.ManageNotifications);
        Assert.Contains(await SearchDestinations.MatchAsync(query, _mgmt.Object, _actor), d => d.Url == url);
    }

    [Fact]
    public void GlobalOnly_IsDeclaredPerEntry_NotDerivedFromTheUrl()
    {
        Assert.True(new SearchDestination("k", [], "/backup", "archive.svg", ManagementPermission.ManageBackups, GlobalOnly: true).GlobalOnly);
        Assert.False(new SearchDestination("k", [], "/settings?tab=new", "gear.svg", ManagementPermission.ManageSystemSettings).GlobalOnly);
        // Every settings tab and the notifications page are checked at global scope by their pages.
        Assert.All(SearchDestinations.All.Where(d => d.Url.StartsWith("/settings", StringComparison.Ordinal) || d.Url == "/notifications"),
            d => Assert.True(d.GlobalOnly, d.Url));
        Assert.All(SearchDestinations.All.Where(d => !d.Url.StartsWith("/settings", StringComparison.Ordinal) && d.Url != "/notifications"),
            d => Assert.False(d.GlobalOnly, d.Url));
    }

    [Fact]
    public void SharePicker_ScopedLinkManagerWithoutAcl_IsNotOfferedTheShare()
    {
        var managed = new ShareDefinition("managed", "/data/managed");
        var readable = new ShareDefinition("readable", "/data/readable");
        _mgmt.Setup(m => m.GetAuthorizedShareIdsAnyAsync(_actor, It.IsAny<ManagementPermission>()))
            .ReturnsAsync(AuthorizedScopeResult.LimitedTo([managed.Id]));
        var acl = new Mock<IAclService>();
        acl.Setup(a => a.HasAccessAsync(_actor, readable.Id, "", true, FilePermission.ListReadData)).ReturnsAsync(true);
        var shares = new Mock<IShareRepository>();
        shares.Setup(s => s.GetAllAsync()).ReturnsAsync([managed, readable]);
        var contexts = new Mock<IUserContextFactory>();
        contexts.Setup(c => c.CreateByUsernameAsync("rita")).ReturnsAsync(_actor);
        var auth = new Mock<AuthenticationStateProvider>();
        auth.Setup(a => a.GetAuthenticationStateAsync()).ReturnsAsync(new AuthenticationState(
            new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.Name, "rita")], "test"))));

        using var ctx = new BunitContext();
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        var env = new Mock<Microsoft.AspNetCore.Hosting.IWebHostEnvironment>();
        env.SetupGet(e => e.WebRootPath).Returns(MailNotificationTests.FindWebRoot());
        ctx.Services.AddSingleton(new AssetProvider(env.Object));
        ctx.Services.AddSingleton(Mock.Of<ISearchService>());
        ctx.Services.AddSingleton(contexts.Object);
        ctx.Services.AddSingleton(_mgmt.Object);
        ctx.Services.AddSingleton(auth.Object);
        ctx.Services.AddSingleton(new FileSelectionCoordinator());
        ctx.Services.AddSingleton(shares.Object);
        ctx.Services.AddSingleton(acl.Object);

        var cut = ctx.Render<GlobalSearch>();
        cut.Find("select.search-scope-select").Change("pick");

        var offered = cut.FindComponent<PrincipalPickerField>().Instance.Items.Select(i => i.Name);
        Assert.Equal(["readable"], offered);
    }
}

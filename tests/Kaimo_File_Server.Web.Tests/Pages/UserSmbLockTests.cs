using System.Security.Claims;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Bunit;
using Kaimo_File_Server.Core.Domain.Identity;
using Kaimo_File_Server.Core.Language;
using Kaimo_File_Server.Core.Repositories;
using Kaimo_File_Server.Core.Security;
using Kaimo_File_Server.Core.Services;
using Kaimo_File_Server.Infrastructure.Configuration;
using Kaimo_File_Server.Web.Components.Pages.Users.Components;
using Kaimo_File_Server.Web.Components.ViewModels;
using Kaimo_File_Server.Web.Services;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;
using Kaimo_File_Server.Web.Tests.Services;

namespace Kaimo_File_Server.Web.Tests.Pages;

/// <summary>
/// User details → SMB lockout row (AP8e): a warning badge while Samba has the account locked,
/// an "Unlock SMB" secondary button for administrators that explains the sync delay, and every
/// key in both language files.
/// </summary>
public partial class UserSmbLockTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);
    private readonly User _anna = new(Guid.NewGuid(), "Anna", "anna.weber", "pw", "nt");
    private readonly Mock<IUserRepository> _users = new();

    [GeneratedRegex("\"(Web_User_SmbLock_\\w+)\"")]
    private static partial Regex SmbLockKey();

    [Fact]
    public void LockedAccount_ShowsTheWarningAndTheUnlockButton()
    {
        using var ctx = new BunitContext();
        var cut = Render(ctx, lockedUntil: DateTime.UtcNow.AddMinutes(10), canUnlock: true);

        cut.WaitForAssertion(() => Assert.NotNull(cut.Find(".status-badge--warning")), Timeout);
        var button = cut.Find("button.btn-secondary");
        Assert.Equal("button", button.GetAttribute("type"));
        Assert.Contains(R("Web_User_SmbLock_Unlock"), button.TextContent);
        Assert.Contains(R("Web_User_SmbLock_Hint"), cut.Markup);
    }

    [Fact]
    public void Unlock_ShowsThatTheSyncAppliesIt()
    {
        using var ctx = new BunitContext();
        var cut = Render(ctx, lockedUntil: DateTime.UtcNow.AddMinutes(10), canUnlock: true);
        cut.WaitForAssertion(() => Assert.NotNull(cut.Find("button.btn-secondary")), Timeout);

        cut.Find("button.btn-secondary").Click();

        cut.WaitForAssertion(() => Assert.Contains(R("Web_User_SmbLock_Requested"), cut.Markup), Timeout);
        Assert.Empty(cut.FindAll(".status-badge--warning"));
        _users.Verify(u => u.RequestSmbUnlockAsync(_anna.Id, It.IsAny<DateTime>()), Times.Once);
    }

    [Fact]
    public void WithoutPermission_OnlyTheStateIsShown()
    {
        using var ctx = new BunitContext();
        var cut = Render(ctx, lockedUntil: null, canUnlock: false);

        cut.WaitForAssertion(() => Assert.Contains(R("Web_User_SmbLock_NotLocked"), cut.Markup), Timeout);
        Assert.Empty(cut.FindAll("button"));
    }

    [Theory]
    [InlineData("Resources.resx")]
    [InlineData("Resources.de.resx")]
    public void EveryKey_ExistsInLanguageFile(string resxFile)
    {
        var root = FindRepoRoot();
        var source = File.ReadAllText(Path.Combine(root, "src/Kaimo_File_Server.Web/Components/Pages/Users/Components/UserSmbLock.razor"));
        var used = SmbLockKey().Matches(source).Select(m => m.Groups[1].Value).ToHashSet();
        var defined = XDocument.Load(Path.Combine(root, "src/Kaimo_File_Server.Core/Language", resxFile))
            .Root!.Elements("data")
            .Where(e => !string.IsNullOrWhiteSpace(e.Element("value")?.Value))
            .Select(e => (string)e.Attribute("name")!)
            .ToHashSet();

        Assert.True(used.Count >= 5, $"Expected the SMB lock keys to be found, got {used.Count}.");
        Assert.Empty(used.Except(defined).Order());
    }

    private IRenderedComponent<UserSmbLock> Render(BunitContext ctx, DateTime? lockedUntil, bool canUnlock)
    {
        var authState = new Mock<AuthenticationStateProvider>();
        authState.Setup(a => a.GetAuthenticationStateAsync()).ReturnsAsync(new AuthenticationState(
            new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.Name, "admin")], "test"))));
        var actor = new UserContext(new User(Guid.NewGuid(), "Admin", "admin", "pw", "nt"), [], [], []);
        var contexts = new Mock<IUserContextFactory>();
        contexts.Setup(c => c.CreateByUsernameAsync("admin")).ReturnsAsync(actor);
        var mgmtAuth = new Mock<IManagementAuthService>();
        mgmtAuth.Setup(m => m.CanManageUserAsync(actor, _anna.Id, ManagementPermission.EnableDisableUsers)).ReturnsAsync(canUnlock);
        var events = new Mock<ISecurityEventRepository>();
        events.Setup(e => e.GetActiveLockUntilAsync(SecurityChannel.Smb, "anna.weber", It.IsAny<DateTime>(), It.IsAny<DateTime>()))
            .ReturnsAsync(lockedUntil);

        ctx.Services.AddSingleton(new DateFormatService(Mock.Of<IConfigRepository>()));
        ctx.Services.AddSingleton(new ToastService());
        var env = new Mock<Microsoft.AspNetCore.Hosting.IWebHostEnvironment>();
        env.SetupGet(e => e.WebRootPath).Returns(MailNotificationTests.FindWebRoot());
        ctx.Services.AddSingleton(new AssetProvider(env.Object));
        ctx.Services.AddScoped(_ => new SmbLockViewModel(authState.Object, contexts.Object, mgmtAuth.Object,
            _users.Object, events.Object, NullLogger<SmbLockViewModel>.Instance));

        return ctx.Render<UserSmbLock>(p => p.Add(c => c.User, _anna));
    }

    private static string R(string key) => Resources.ResourceManager.GetString(key) ?? key;

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Kaimo_File_Server.slnx")))
            dir = dir.Parent;
        return dir?.FullName ?? throw new DirectoryNotFoundException("Repository root not found.");
    }
}

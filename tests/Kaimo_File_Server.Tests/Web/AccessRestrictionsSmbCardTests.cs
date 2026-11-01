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
using Kaimo_File_Server.Infrastructure.Services;
using Kaimo_File_Server.Web.Components.Pages.Settings.Components;
using Kaimo_File_Server.Web.Components.ViewModels;
using Kaimo_File_Server.Web.Services;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Kaimo_File_Server.Tests;

/// <summary>
/// Settings → Access restrictions → SMB card (AP8c): a labelled network list with the Docker
/// proxy and recovery-switch hints, its own save button, and every key in both language files.
/// </summary>
public partial class AccessRestrictionsSmbCardTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);

    [GeneratedRegex("\"(Web_AccessRestrictions_Smb_\\w+)\"")]
    private static partial Regex SmbKey();

    [Fact]
    public void NetworkList_IsLabelledAndShowsTheStoredNetworks()
    {
        using var ctx = new BunitContext();
        var cut = Render(ctx, ["10.0.0.0/8"]);

        cut.WaitForAssertion(() => Assert.NotNull(cut.Find("#access-smb-networks")), Timeout);
        Assert.Single(cut.FindAll("label[for='access-smb-networks']"));
        Assert.Equal("10.0.0.0/8", cut.Find("#access-smb-networks").GetAttribute("value"));
        Assert.NotNull(cut.Find(".access-smb .btn-primary"));
    }

    [Fact]
    public void ProxyAndRecoveryHints_AreShown()
    {
        using var ctx = new BunitContext();
        var cut = Render(ctx, []);
        var proxy = Resources.ResourceManager.GetString("Web_AccessRestrictions_Smb_ProxyHint");
        var recovery = Resources.ResourceManager.GetString("Web_AccessRestrictions_Smb_RecoveryHint");
        Assert.False(string.IsNullOrEmpty(proxy));
        Assert.False(string.IsNullOrEmpty(recovery));

        cut.WaitForAssertion(() => Assert.NotNull(cut.Find(".access-smb")), Timeout);
        var card = cut.Find(".access-smb");
        Assert.Contains(card.QuerySelectorAll(".info-btn"), b => b.TextContent.Contains(proxy!));
        Assert.Contains(recovery!, card.TextContent);
        Assert.Contains(AccessRestrictionSettings.DisableEnvironmentVariable, recovery);
    }

    [Theory]
    [InlineData("Resources.resx")]
    [InlineData("Resources.de.resx")]
    public void EverySmbKey_ExistsInLanguageFile(string resxFile)
    {
        var root = FindRepoRoot();
        var source = File.ReadAllText(Path.Combine(root,
            "src/Kaimo_File_Server.Web/Components/Pages/Settings/Components/AccessRestrictionsTab.razor"));
        var used = SmbKey().Matches(source).Select(m => m.Groups[1].Value).ToHashSet();
        var defined = XDocument.Load(Path.Combine(root, "src/Kaimo_File_Server.Core/Language", resxFile))
            .Root!.Elements("data")
            .Where(e => !string.IsNullOrWhiteSpace(e.Element("value")?.Value))
            .Select(e => (string)e.Attribute("name")!)
            .ToHashSet();

        Assert.True(used.Count >= 5, $"Expected the SMB card keys to be found, got {used.Count}.");
        Assert.Empty(used.Except(defined).Order());
    }

    private static IRenderedComponent<AccessRestrictionsTab> Render(BunitContext ctx, List<string> smbNetworks)
    {
        var env = new Mock<Microsoft.AspNetCore.Hosting.IWebHostEnvironment>();
        env.SetupGet(e => e.WebRootPath).Returns(MailNotificationTests.FindWebRoot());
        ctx.Services.AddSingleton(new AssetProvider(env.Object));

        var authState = new Mock<AuthenticationStateProvider>();
        authState.Setup(a => a.GetAuthenticationStateAsync()).ReturnsAsync(new AuthenticationState(
            new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.Name, "admin")], "test"))));
        var actor = new UserContext(new User(Guid.NewGuid(), "Admin", "admin", "pw", "nt"), [], [], []);
        var contexts = new Mock<IUserContextFactory>();
        contexts.Setup(c => c.CreateByUsernameAsync("admin")).ReturnsAsync(actor);
        var mgmtAuth = new Mock<IManagementAuthService>();
        mgmtAuth.Setup(m => m.GetEffectivePermissionsAtAsync(actor, ScopeType.Global, Guid.Empty))
            .ReturnsAsync(ManagementPermission.ManageSystemSettings);
        var config = new Mock<IConfigRepository>();
        config.Setup(c => c.GetAsync(AccessRestrictionSettings.ConfigKey, It.IsAny<AccessRestrictionSettings>()))
            .ReturnsAsync(new AccessRestrictionSettings { SmbAllowedNetworks = smbNetworks });
        config.Setup(c => c.GetAsync(ServerDomainSettings.ConfigKey, It.IsAny<ServerDomainSettings>()))
            .ReturnsAsync(new ServerDomainSettings());
        var users = new Mock<IUserRepository>();
        users.Setup(u => u.GetAllAsync()).ReturnsAsync([]);
        var groups = new Mock<IGroupRepository>();
        groups.Setup(g => g.GetAllAsync()).ReturnsAsync([]);
        var departments = new Mock<IDepartmentRepository>();
        departments.Setup(d => d.GetAllAsync()).ReturnsAsync([]);
        var access = new AccessRestrictionService(config.Object, contexts.Object, departments.Object,
            new MemoryCache(new MemoryCacheOptions()));

        ctx.Services.AddScoped(_ => new AccessSettingsViewModel(authState.Object, contexts.Object, mgmtAuth.Object,
            config.Object, access, users.Object, groups.Object, departments.Object,
            new ClientConnectionInfo { RemoteAddress = "10.0.0.5", Host = "files.example.com" },
            NullLogger<AccessSettingsViewModel>.Instance));

        return ctx.Render<AccessRestrictionsTab>();
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Kaimo_File_Server.slnx")))
            dir = dir.Parent;
        return dir?.FullName ?? throw new DirectoryNotFoundException("Repository root not found.");
    }
}

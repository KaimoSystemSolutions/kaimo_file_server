using System.Security.Claims;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Bunit;
using Kaimo_File_Server.Core.Domain.Identity;
using Kaimo_File_Server.Core.Language;
using Kaimo_File_Server.Core.Repositories.Identity;
using Kaimo_File_Server.Core.Security.Management;
using Kaimo_File_Server.Core.Services.Management;
using Kaimo_File_Server.Infrastructure.Configuration;
using Kaimo_File_Server.Web.Components.Pages.Settings;
using Kaimo_File_Server.Web.Components.Pages.Settings.Components;
using Kaimo_File_Server.Web.Components.Search;
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
/// Settings → Login protection tab: every field is labelled, the lockout risk is explained,
/// invalid input is marked on the field, the tab is reachable through search and deep link,
/// and every user-facing key exists in both language files.
/// </summary>
public partial class LoginProtectionSettingsTabTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);

    private static readonly string[] SourceFiles =
    [
        "src/Kaimo_File_Server.Web/Components/Pages/Settings/Components/LoginProtectionSettingsTab.razor",
        "src/Kaimo_File_Server.Web/Components/ViewModels/Settings/LoginProtectionSettingsViewModel.cs",
        "src/Kaimo_File_Server.Web/Components/Pages/Settings/Settings.razor",
    ];

    [GeneratedRegex("\"(Web_LoginProtection_\\w+|Web_Settings_Tab_LoginProtection)\"")]
    private static partial Regex LoginProtectionKey();

    [Fact]
    public void EveryInput_HasALabel()
    {
        using var ctx = new BunitContext();
        var cut = Render(ctx, canManage: true);
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("input")), Timeout);

        var inputs = cut.FindAll("input");
        Assert.Equal(11, inputs.Count);
        foreach (var input in inputs)
        {
            var id = input.GetAttribute("id");
            var labelled = !string.IsNullOrEmpty(input.GetAttribute("aria-label"))
                           || (!string.IsNullOrEmpty(id) && cut.FindAll($"label[for='{id}']").Count == 1);
            Assert.True(labelled, $"Input '{id}' has no label.");
        }
    }

    [Fact]
    public void LockoutRisk_IsExplainedByAnInfoButton()
    {
        using var ctx = new BunitContext();
        var cut = Render(ctx, canManage: true);
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll(".info-btn")), Timeout);

        var hint = Resources.ResourceManager.GetString("Web_LoginProtection_LockoutRiskHint");
        Assert.False(string.IsNullOrEmpty(hint));
        Assert.Contains(cut.FindAll(".info-btn"), b => b.TextContent.Contains(hint!));
    }

    [Fact]
    public void SmbCard_WarnsThatTheLockoutIsAccountWide()
    {
        using var ctx = new BunitContext();
        var cut = Render(ctx, canManage: true);
        var warning = Resources.ResourceManager.GetString("Web_LoginProtection_Smb_GlobalWarning");

        cut.WaitForAssertion(() => Assert.Contains(warning!, cut.Find(".warning-banner").TextContent), Timeout);
    }

    [Fact]
    public void InvalidValue_IsMarkedOnTheField_AfterSave()
    {
        using var ctx = new BunitContext();
        var cut = Render(ctx, canManage: true);
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("input")), Timeout);

        cut.Find("#login-protection-max-attempts").Change("0");
        cut.Find("button.btn-primary").Click();

        cut.WaitForAssertion(() =>
        {
            var input = cut.Find("#login-protection-max-attempts");
            Assert.Equal("true", input.GetAttribute("aria-invalid"));
            Assert.NotEmpty(cut.FindAll("#login-protection-max-attempts-error"));
        }, Timeout);
    }

    [Fact]
    public void CorrectedValue_RemovesTheFieldMarker_BeforeTheNextSave()
    {
        using var ctx = new BunitContext();
        var cut = Render(ctx, canManage: true);
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("input")), Timeout);
        cut.Find("#login-protection-max-attempts").Change("0");
        cut.Find("button.btn-primary").Click();
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("#login-protection-max-attempts-error")), Timeout);

        cut.Find("#login-protection-max-attempts").Change("6");

        cut.WaitForAssertion(() =>
        {
            Assert.Null(cut.Find("#login-protection-max-attempts").GetAttribute("aria-invalid"));
            Assert.Empty(cut.FindAll("#login-protection-max-attempts-error"));
        }, Timeout);
    }

    [Fact]
    public void FieldErrors_AreNotAlerts_OnlyTheBannerIs()
    {
        using var ctx = new BunitContext();
        var cut = Render(ctx, canManage: true);
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("input")), Timeout);
        cut.Find("#login-protection-max-attempts").Change("0");
        cut.Find("#login-protection-lockout-minutes").Change("0");
        cut.Find("button.btn-primary").Click();

        cut.WaitForAssertion(() => Assert.Equal(2, cut.FindAll(".login-protection-field-error").Count), Timeout);
        Assert.Single(cut.FindAll("[role='alert']"));
        Assert.Contains("error-banner", cut.Find("[role='alert']").ClassList);
    }

    [Fact]
    public void InvalidMaxAttempts_ShowsTheFactorHint_WithoutACount()
    {
        using var ctx = new BunitContext();
        var cut = Render(ctx, canManage: true);
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("input")), Timeout);

        cut.Find("#login-protection-max-attempts").Change("0");

        var generic = Resources.ResourceManager.GetString("Web_LoginProtection_AccountWideFactor_HintUnknown");
        Assert.False(string.IsNullOrEmpty(generic));
        cut.WaitForAssertion(() =>
            Assert.Equal(generic, cut.Find("#login-protection-account-factor-hint").TextContent.Trim()), Timeout);
    }

    [Fact]
    public void DisabledSmbLockout_DisablesTheSmbFields_Only()
    {
        using var ctx = new BunitContext();
        var cut = Render(ctx, canManage: true);
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("input")), Timeout);
        Assert.Null(cut.Find("#login-protection-smb-attempts").GetAttribute("disabled"));

        cut.Find("input[type='checkbox']").Change(false);

        cut.WaitForAssertion(() =>
        {
            Assert.NotNull(cut.Find("#login-protection-smb-attempts").GetAttribute("disabled"));
            Assert.NotNull(cut.Find("#login-protection-smb-lockout-minutes").GetAttribute("disabled"));
        }, Timeout);
        Assert.Null(cut.Find("#login-protection-max-attempts").GetAttribute("disabled"));
    }

    // Every card is in effect: share links since AP5, the SMB lockout since AP8.
    [Fact]
    public void NoCard_IsMarkedAsNotYetEffective()
    {
        using var ctx = new BunitContext();
        var cut = Render(ctx, canManage: true);

        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("input")), Timeout);
        var sections = cut.FindAll(".detail-section");
        Assert.Equal(3, sections.Count);
        Assert.All(sections, section =>
        {
            Assert.Empty(section.QuerySelectorAll(".status-badge"));
            Assert.Null(section.QuerySelector(".login-protection-pending"));
        });
    }

    [Fact]
    public void NumberField_RequiresMax()
    {
        var max = typeof(LoginProtectionNumberField).GetProperty(nameof(LoginProtectionNumberField.Max))!;

        Assert.NotNull(max.GetCustomAttributes(typeof(Microsoft.AspNetCore.Components.EditorRequiredAttribute), false).SingleOrDefault());
    }

    [Fact]
    public void EveryCard_HasAHeaderIcon()
    {
        using var ctx = new BunitContext();
        var cut = Render(ctx, canManage: true);

        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("input")), Timeout);
        Assert.All(cut.FindAll(".detail-section"), section =>
            Assert.NotNull(section.QuerySelector(".login-protection-card-head .tile-icon svg")));
    }

    // Each number field is one row (icon | label + hint | input), so a label or hint that wraps
    // only grows its own row instead of shifting the inputs of neighbouring fields.
    [Fact]
    public void EveryNumberField_IsARowWithIconTextAndControl()
    {
        using var ctx = new BunitContext();
        var cut = Render(ctx, canManage: true);

        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("input")), Timeout);
        var fields = cut.FindAll(".login-protection-field");
        Assert.Equal(10, fields.Count);
        Assert.All(fields, field =>
        {
            Assert.NotNull(field.QuerySelector(".login-protection-field-icon svg"));
            Assert.NotNull(field.QuerySelector(".login-protection-field-text label"));
            Assert.NotNull(field.QuerySelector(".login-protection-field-control input[type='number']"));
        });
    }

    [Fact]
    public void NumberField_RequiresIcon()
    {
        var icon = typeof(LoginProtectionNumberField).GetProperty("Icon");

        Assert.NotNull(icon?.GetCustomAttributes(typeof(Microsoft.AspNetCore.Components.EditorRequiredAttribute), false).SingleOrDefault());
    }

    [Fact]
    public void SearchDestination_DoesNotPromiseTheApiRateLimit()
    {
        var destination = Assert.Single(SearchDestinations.All, d => d.Url == "/settings?tab=loginprotection");

        Assert.DoesNotContain(destination.Keywords, k => k.Contains("rate limit"));
    }

    [Fact]
    public void WithoutPermission_ShowsNoForm()
    {
        using var ctx = new BunitContext();
        var cut = Render(ctx, canManage: false);

        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll(".error-banner")), Timeout);
        Assert.Empty(cut.FindAll("input"));
    }

    [Fact]
    public void SearchDestination_OpensTheLoginProtectionTab()
    {
        var destination = Assert.Single(SearchDestinations.All, d => d.Url == "/settings?tab=loginprotection");

        Assert.Equal("Web_Settings_Tab_LoginProtection", destination.TitleKey);
        Assert.Equal(ManagementPermission.ManageSystemSettings, destination.RequiredAny);
    }

    [Theory]
    [InlineData("loginprotection")]
    [InlineData("LoginProtection")]
    public void SettingsTab_DeepLink_IsRecognized(string query)
    {
        Assert.True(Settings.TryParseTab(query, out var tab));
        Assert.Equal(SettingsTab.LoginProtection, tab);
    }

    [Fact]
    public void SettingsPage_ShowsTheTabOnlyForSettingsManagers()
    {
        var page = File.ReadAllText(Path.Combine(FindRepoRoot(), SourceFiles[2]));

        Assert.Contains("case SettingsTab.LoginProtection when VM.CanManageSettings:", page);
    }

    [Theory]
    [InlineData("Resources.resx")]
    [InlineData("Resources.de.resx")]
    public void EveryLoginProtectionKey_ExistsInLanguageFile(string resxFile)
    {
        var root = FindRepoRoot();
        var used = SourceFiles
            .SelectMany(f => LoginProtectionKey().Matches(File.ReadAllText(Path.Combine(root, f))))
            .Select(m => m.Groups[1].Value)
            .ToHashSet();
        var defined = XDocument.Load(Path.Combine(root, "src/Kaimo_File_Server.Core/Language", resxFile))
            .Root!.Elements("data")
            .Where(e => !string.IsNullOrWhiteSpace(e.Element("value")?.Value))
            .Select(e => (string)e.Attribute("name")!)
            .ToHashSet();

        Assert.True(used.Count > 20, $"Expected the login protection keys to be found, got {used.Count}.");
        Assert.Empty(used.Except(defined).Order());
    }

    private static IRenderedComponent<LoginProtectionSettingsTab> Render(BunitContext ctx, bool canManage)
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
        mgmtAuth.Setup(m => m.HasGlobalPermissionAsync(actor, ManagementPermission.ManageSystemSettings))
            .ReturnsAsync(canManage);
        var config = new Mock<IConfigRepository>();
        config.Setup(c => c.GetIntAsync(It.IsAny<string>(), It.IsAny<int>())).ReturnsAsync((string _, int fallback) => fallback);
        config.Setup(c => c.GetBoolAsync(It.IsAny<string>(), It.IsAny<bool>())).ReturnsAsync((string _, bool fallback) => fallback);

        ctx.Services.AddScoped(_ => new LoginProtectionSettingsViewModel(authState.Object, contexts.Object,
            mgmtAuth.Object, config.Object, NullLogger<LoginProtectionSettingsViewModel>.Instance));

        return ctx.Render<LoginProtectionSettingsTab>();
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Kaimo_File_Server.slnx")))
            dir = dir.Parent;
        return dir?.FullName ?? throw new DirectoryNotFoundException("Repository root not found.");
    }
}

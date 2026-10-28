using Bunit;
using Kaimo_File_Server.Core.Services.File;
using Kaimo_File_Server.Infrastructure.Services;
using Kaimo_File_Server.Web.Components.Pages.Settings.Components;
using Kaimo_File_Server.Web.Services;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using Xunit;

namespace Kaimo_File_Server.Tests;

/// <summary>Settings → Versioning: loads the global policy, edits it and persists it through the store.</summary>
public class VersioningSettingsTabTests
{
    private readonly Mock<IVersioningPolicyStore> _store = new();
    private VersioningPolicy? _saved;
    private readonly ToastService _toasts = new();

    private BunitContext CreateContext(VersioningPolicy? stored = null)
    {
        var ctx = new BunitContext();
        var env = new Mock<Microsoft.AspNetCore.Hosting.IWebHostEnvironment>();
        env.SetupGet(e => e.WebRootPath).Returns(MailNotificationTests.FindWebRoot());
        ctx.Services.AddSingleton(new AssetProvider(env.Object));
        _store.Setup(s => s.GetAsync()).ReturnsAsync(() => (stored ?? VersioningPolicy.Default()).Clone());
        _store.Setup(s => s.SetAsync(It.IsAny<VersioningPolicy>()))
            .Callback<VersioningPolicy>(p => { _saved = p; stored = p; })
            .Returns(Task.CompletedTask);
        ctx.Services.AddSingleton(_store.Object);
        ctx.Services.AddSingleton(_toasts);
        return ctx;
    }

    [Fact]
    public void Render_ShowsStoredValues()
    {
        using var ctx = CreateContext(new VersioningPolicy { MaxFileSizeBytes = 256L * 1024 * 1024, MaxAgeDays = 45 });

        var cut = ctx.Render<VersioningSettingsTab>(p => p.Add(c => c.CanManage, true));

        Assert.Equal("256", cut.Find("#versioning-max-size").GetAttribute("value"));
        Assert.Equal("45", cut.Find("#versioning-max-age").GetAttribute("value"));
        Assert.Contains("~$*", cut.Find("#versioning-patterns").GetAttribute("value") ?? cut.Find("#versioning-patterns").TextContent);
    }

    [Fact]
    public void Save_PersistsEditedValues()
    {
        using var ctx = CreateContext();
        var cut = ctx.Render<VersioningSettingsTab>(p => p.Add(c => c.CanManage, true));

        cut.Find("#versioning-max-size").Change("100");
        cut.Find("#versioning-max-versions").Change("12");
        cut.Find("#versioning-budget").Change("25");
        cut.Find("#versioning-patterns").Input("*.bak\n\n  *.log  ");
        cut.Find("#versioning-enabled").Change(false);
        cut.Find(".versioning-save").Click();

        Assert.NotNull(_saved);
        Assert.False(_saved.Enabled);
        Assert.Equal(100L * 1024 * 1024, _saved.MaxFileSizeBytes);
        Assert.Equal(12, _saved.MaxVersionsPerFile);
        Assert.Equal(25, _saved.PoolBudgetPercent);
        Assert.Equal(["*.bak", "*.log"], _saved.ExcludePatterns);
        // Feedback is a toast: an in-tab banner sits above the fold once the page is scrolled down.
        Assert.Equal(ToastType.Success, Assert.Single(_toasts.Toasts).Type);
        Assert.Empty(cut.FindAll(".success-banner"));
    }

    [Fact]
    public void Save_Failure_ShowsErrorToastAndBanner()
    {
        using var ctx = CreateContext();
        _store.Setup(s => s.SetAsync(It.IsAny<VersioningPolicy>())).ThrowsAsync(new IOException("disk full"));
        var cut = ctx.Render<VersioningSettingsTab>(p => p.Add(c => c.CanManage, true));

        cut.Find(".versioning-save").Click();

        Assert.Equal(ToastType.Error, Assert.Single(_toasts.Toasts).Type);
        Assert.NotEmpty(cut.FindAll(".error-banner"));
    }

    [Fact]
    public void RestoreDefaultPatterns_RefillsTheList()
    {
        using var ctx = CreateContext(new VersioningPolicy { ExcludePatterns = [] });
        var cut = ctx.Render<VersioningSettingsTab>(p => p.Add(c => c.CanManage, true));

        cut.Find(".versioning-default-patterns").Click();
        cut.Find(".versioning-save").Click();

        Assert.Equal(VersioningPolicy.DefaultExcludePatterns, _saved!.ExcludePatterns);
    }

    [Fact]
    public void WithoutPermission_SaveIsDisabledAndNothingIsStored()
    {
        using var ctx = CreateContext();
        var cut = ctx.Render<VersioningSettingsTab>(p => p.Add(c => c.CanManage, false));

        Assert.True(cut.Find(".versioning-save").HasAttribute("disabled"));
        _store.Verify(s => s.SetAsync(It.IsAny<VersioningPolicy>()), Times.Never);
    }

    [Fact]
    public void StoreFailure_ShowsError()
    {
        using var ctx = CreateContext();
        _store.Setup(s => s.SetAsync(It.IsAny<VersioningPolicy>())).ThrowsAsync(new InvalidOperationException("db"));
        var cut = ctx.Render<VersioningSettingsTab>(p => p.Add(c => c.CanManage, true));

        cut.Find(".versioning-save").Click();

        Assert.NotEmpty(cut.FindAll(".error-banner"));
    }

    [Fact]
    public void StorageLink_IsAStyledButtonToTheStorageTab()
    {
        using var ctx = CreateContext();
        var cut = ctx.Render<VersioningSettingsTab>(p => p.Add(c => c.CanManage, true));

        var link = cut.Find("a.versioning-storage-link");
        Assert.Equal("/settings?tab=storage", link.GetAttribute("href"));
        Assert.Contains("btn-secondary", link.ClassList);
        Assert.Empty(cut.FindAll(".settings-hint a"));
    }

    [Fact]
    public void ThinningOff_DisablesTierFields()
    {
        using var ctx = CreateContext(new VersioningPolicy { ThinningEnabled = false });
        var cut = ctx.Render<VersioningSettingsTab>(p => p.Add(c => c.CanManage, true));

        Assert.True(cut.Find("#versioning-keep-all").HasAttribute("disabled"));
        Assert.True(cut.Find("#versioning-hourly").HasAttribute("disabled"));
        Assert.True(cut.Find("#versioning-daily").HasAttribute("disabled"));
    }
}

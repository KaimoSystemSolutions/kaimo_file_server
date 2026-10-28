using System.Text.RegularExpressions;
using System.Xml.Linq;
using Bunit;
using Kaimo_File_Server.Web.Components.Shared;
using Kaimo_File_Server.Web.Components.ViewModels.Backup;
using Kaimo_File_Server.Web.Services;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using Xunit;

namespace Kaimo_File_Server.Tests;

/// <summary>
/// The backup pages follow the repository tab's design: pools are picked with the shared
/// principal picker (own kind, icon and color), and the German texts say "Share" like the
/// rest of the German UI instead of "Freigabe".
/// </summary>
public class BackupDesignAlignmentTests
{
    [Fact]
    public void PoolId_IsStablePerPath()
        => Assert.Equal(PrincipalPickerBindings.PoolId("/mnt/pool01"), PrincipalPickerBindings.PoolId("/mnt/pool01"));

    [Fact]
    public void PoolId_DiffersBetweenPaths()
        => Assert.NotEqual(PrincipalPickerBindings.PoolId("/mnt/pool01"), PrincipalPickerBindings.PoolId("/mnt/pool02"));

    [Fact]
    public void PoolItems_RoundTripThePickedPath()
    {
        string[] paths = ["/mnt/pool01", "/mnt/pool02"];
        var items = paths.ToPoolItems(p => p, p => p[5..]);

        Assert.All(items, i => Assert.Equal(PrincipalKind.Pool, i.Kind));
        Assert.Equal("pool02", items[1].Name);
        Assert.Equal("/mnt/pool02", items[1].Hint);
        Assert.Equal("/mnt/pool02", paths.PoolPathFor([items[1].Id]));
        Assert.Null(paths.PoolPathFor([]));
        Assert.Equal([items[0].Id], "/mnt/pool01".AsPoolSelection());
    }

    [Fact]
    public void PoolIcon_KeepsItsEllipse()
    {
        var svg = NewAssets().SVG("pool.svg", new Kaimo_File_Server.Web.DynamicHelpers.SvgOptions()).Value;

        Assert.Contains("<ellipse", svg);
    }

    [Fact]
    public void RetentionRun_HasAFilterIconInsteadOfATrashCan()
    {
        Assert.Equal("filter.svg", BackupUi.RunIcon(Kaimo_File_Server.Core.Domain.Backup.BackupRunType.Forget));
        Assert.Contains("<polygon", NewAssets().SVG("filter.svg", new Kaimo_File_Server.Web.DynamicHelpers.SvgOptions()).Value);
    }

    [Fact]
    public void Picker_Disabled_DoesNotOpen()
    {
        using var ctx = NewContext();
        var cut = ctx.Render<PrincipalPickerField>(p => p
            .Add(f => f.Mode, SelectionMode.Single)
            .Add(f => f.Items, [new PrincipalPickerItem(Guid.NewGuid(), "pool01", PrincipalKind.Pool)])
            .Add(f => f.Disabled, true));

        var trigger = cut.Find(".principal-picker-trigger");
        Assert.Equal("true", trigger.GetAttribute("aria-disabled"));
        trigger.Click();
        Assert.Empty(cut.FindAll(".principal-picker-dialog"));
    }

    [Theory]
    [InlineData("src/Kaimo_File_Server.Web/Components/Pages/Shares/Components/ShareCreateForm.razor")]
    [InlineData("src/Kaimo_File_Server.Web/Components/Pages/Shares/Components/ShareExtraPanel.razor")]
    [InlineData("src/Kaimo_File_Server.Web/Components/Pages/Settings/Components/HomeFolderSettings.razor")]
    public void PoolSelection_UsesTheSharedPicker(string file)
    {
        var markup = File.ReadAllText(Path.Combine(FindRepoRoot(), file));

        Assert.Contains("ToPoolItems(", markup);
        Assert.DoesNotContain("<select", markup);
    }

    [Fact]
    public void Picker_SelectedPool_UsesThePoolEntityColor()
    {
        using var ctx = NewContext();
        var pool = new PrincipalPickerItem(Guid.NewGuid(), "pool01", PrincipalKind.Pool);

        var cut = ctx.Render<PrincipalPickerField>(p => p
            .Add(f => f.Items, [pool])
            .Add(f => f.SelectedIds, [pool.Id]));

        Assert.Contains("var(--entity-pool)", cut.Find(".principal-picker-chip").GetAttribute("style"));
    }

    [Fact]
    public void Picker_WithSharesAndPools_ShowsAPoolTab()
    {
        using var ctx = NewContext();
        var cut = ctx.Render<PrincipalPickerField>(p => p
            .Add(f => f.Items, [
                new PrincipalPickerItem(Guid.NewGuid(), "Projects", PrincipalKind.Share),
                new PrincipalPickerItem(Guid.NewGuid(), "pool01", PrincipalKind.Pool)]));

        cut.Find(".principal-picker-trigger").Click();

        Assert.Contains(cut.FindAll(".principal-picker-tab"), t => t.TextContent.Contains("Pools"));
    }

    [Fact]
    public void JobEditor_PicksPoolsWithTheSharedPicker()
    {
        var jobs = File.ReadAllText(Path.Combine(FindRepoRoot(), "src/Kaimo_File_Server.Web/Components/Pages/Backup/Components/BackupJobs.razor"));

        Assert.Contains("ToPoolItems(", jobs);
        Assert.DoesNotContain("<MultiSelect", jobs);
    }

    [Fact]
    public void PoolTabLabel_ExistsInBothLanguages()
    {
        Assert.Contains("Web_Picker_Pools", Keys("Resources.resx"));
        Assert.Contains("Web_Picker_Pools", Keys("Resources.de.resx"));
    }

    [Fact]
    public void OverviewKeys_ExistInBothLanguages()
    {
        var overview = File.ReadAllText(Path.Combine(FindRepoRoot(), "src/Kaimo_File_Server.Web/Components/Pages/Backup/Components/BackupOverview.razor"));
        var used = Regex.Matches(overview, "\"(Web_\\w+)\"").Select(m => m.Groups[1].Value).Distinct().ToList();
        var en = Keys("Resources.resx");
        var de = Keys("Resources.de.resx");

        Assert.NotEmpty(used);
        Assert.DoesNotContain(used, k => !en.Contains(k) || !de.Contains(k));
    }

    [Fact]
    public void GermanBackupTexts_SayShareInsteadOfFreigabe()
    {
        var offending = Values("Resources.de.resx")
            .Where(kv => (kv.Key.StartsWith("Web_Backup_", StringComparison.Ordinal) || kv.Key.StartsWith("Web_Picker_", StringComparison.Ordinal))
                         && Regex.IsMatch(kv.Value, "[Ff]reigabe"))
            .Select(kv => kv.Key)
            .ToList();

        Assert.Empty(offending);
    }

    private static Dictionary<string, string> Values(string file)
        => XDocument.Load(Path.Combine(FindRepoRoot(), "src/Kaimo_File_Server.Core/Language", file))
            .Root!.Elements("data")
            .ToDictionary(e => (string)e.Attribute("name")!, e => (string?)e.Element("value") ?? string.Empty);

    private static HashSet<string> Keys(string file) => Values(file).Keys.ToHashSet();

    private static BunitContext NewContext()
    {
        var ctx = new BunitContext();
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        ctx.Services.AddSingleton(NewAssets());
        return ctx;
    }

    private static AssetProvider NewAssets()
    {
        var env = new Mock<Microsoft.AspNetCore.Hosting.IWebHostEnvironment>();
        env.SetupGet(e => e.WebRootPath).Returns(MailNotificationTests.FindWebRoot());
        return new AssetProvider(env.Object);
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Kaimo_File_Server.slnx")))
            dir = dir.Parent;
        return dir?.FullName ?? throw new DirectoryNotFoundException("Repository root not found.");
    }
}

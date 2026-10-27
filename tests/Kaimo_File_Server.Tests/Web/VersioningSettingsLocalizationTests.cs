using System.Text.RegularExpressions;
using System.Xml.Linq;
using Kaimo_File_Server.Core.Security;
using Kaimo_File_Server.Web.Components.Pages.Settings;
using Kaimo_File_Server.Web.Components.Search;
using Xunit;

namespace Kaimo_File_Server.Tests;

/// <summary>
/// Settings → Versioning and the restore-without-backup dialog: reachable through the global
/// search and a deep link, and every user-facing key exists in both language files.
/// </summary>
public partial class VersioningSettingsLocalizationTests
{
    private static readonly string[] SourceFiles =
    [
        "src/Kaimo_File_Server.Web/Components/Pages/Settings/Components/VersioningSettingsTab.razor",
        "src/Kaimo_File_Server.Web/Components/Pages/Settings/Components/VersioningSettingsTab.razor.cs",
        "src/Kaimo_File_Server.Web/Components/Pages/Settings/Settings.razor",
        "src/Kaimo_File_Server.Web/Components/Pages/Files/FileBrowser/components/VersionDialog.razor",
        "src/Kaimo_File_Server.Web/Components/ViewModels/Files/FileBrowserViewModel.Versioning.cs",
    ];

    [GeneratedRegex("\"(Web_VersioningSettings_\\w+|Web_Version_NoBackup_\\w+|Web_Settings_Tab_Versioning)\"")]
    private static partial Regex VersioningKey();

    [Fact]
    public void SearchDestination_ForVersioningSettings_OpensTheSettingsTab()
    {
        var destination = Assert.Single(SearchDestinations.All, d => d.Url == "/settings?tab=versioning");

        Assert.Equal("Web_Settings_Tab_Versioning", destination.TitleKey);
        Assert.Equal(ManagementPermission.ManageSystemSettings, destination.RequiredAny);
    }

    [Theory]
    [InlineData("versioning")]
    [InlineData("Versioning")]
    public void SettingsTab_VersioningDeepLink_IsRecognized(string query)
    {
        Assert.True(Settings.TryParseTab(query, out var tab));
        Assert.Equal(SettingsTab.Versioning, tab);
    }

    [Theory]
    [InlineData("Resources.resx")]
    [InlineData("Resources.de.resx")]
    public void EveryVersioningKey_ExistsInLanguageFile(string resxFile)
    {
        var root = FindRepoRoot();
        var used = SourceFiles
            .SelectMany(f => VersioningKey().Matches(File.ReadAllText(Path.Combine(root, f))))
            .Select(m => m.Groups[1].Value)
            .ToHashSet();
        var defined = XDocument.Load(Path.Combine(root, "src/Kaimo_File_Server.Core/Language", resxFile))
            .Root!.Elements("data")
            .Where(e => !string.IsNullOrWhiteSpace(e.Element("value")?.Value))
            .Select(e => (string)e.Attribute("name")!)
            .ToHashSet();

        Assert.True(used.Count > 30, $"Expected the versioning keys to be found, got {used.Count}.");
        Assert.Empty(used.Except(defined).Order());
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Kaimo_File_Server.slnx")))
            dir = dir.Parent;
        return dir?.FullName ?? throw new DirectoryNotFoundException("Repository root not found.");
    }
}

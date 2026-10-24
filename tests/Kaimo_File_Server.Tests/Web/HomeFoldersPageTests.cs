using Kaimo_File_Server.Core.Security;
using Kaimo_File_Server.Web.Components.Pages.HomeFolders;
using Kaimo_File_Server.Web.Components.Search;
using Xunit;

namespace Kaimo_File_Server.Tests;

/// <summary>
/// The home-folder administration page (/home-folders): tab selection per permission and
/// the global-search entry that leads there instead of the former Settings tab.
/// </summary>
public class HomeFoldersPageTests
{
    [Theory]
    [InlineData(null, true, true, HomeFoldersTab.Overview)]
    [InlineData("settings", true, true, HomeFoldersTab.Settings)]
    [InlineData("SETTINGS", true, true, HomeFoldersTab.Settings)]
    [InlineData("overview", true, true, HomeFoldersTab.Overview)]
    [InlineData("unknown", true, true, HomeFoldersTab.Overview)]
    // Only the settings permission: the overview is not offered.
    [InlineData(null, false, true, HomeFoldersTab.Settings)]
    [InlineData("overview", false, true, HomeFoldersTab.Settings)]
    // Only ManageHomes: a settings deep link falls back to the overview.
    [InlineData("settings", true, false, HomeFoldersTab.Overview)]
    public void ResolveTab_PicksRequestedTabWhenPermitted(
        string? query, bool canOverview, bool canSettings, HomeFoldersTab expected)
    {
        Assert.Equal(expected, HomeFolders.ResolveTab(query, canOverview, canSettings));
    }

    [Fact]
    public void ResolveTab_WithoutAnyPermission_ReturnsNull()
    {
        Assert.Null(HomeFolders.ResolveTab("overview", canOverview: false, canSettings: false));
    }

    [Fact]
    public void SearchDestination_ForHomeFolders_OpensTheHomeFoldersPage()
    {
        var destination = Assert.Single(SearchDestinations.All, d => d.Keywords.Contains("home-ordner"));

        Assert.StartsWith("/home-folders", destination.Url);
        Assert.DoesNotContain(SearchDestinations.All, d => d.Url.Contains("tab=homes"));
        // Both holders of the page see the entry: ManageHomes (overview) and system settings (settings tab).
        Assert.Equal(
            ManagementPermission.ManageHomes | ManagementPermission.ManageSystemSettings,
            destination.RequiredAny);
    }
}

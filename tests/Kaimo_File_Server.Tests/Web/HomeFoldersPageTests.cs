using Kaimo_File_Server.Core.Security;
using Kaimo_File_Server.Web.Components.Pages.Settings;
using Kaimo_File_Server.Web.Components.Search;
using Xunit;

namespace Kaimo_File_Server.Tests;

/// <summary>
/// Home folders: the metadata overview lives on /home-folders (ManageHomes), the setup,
/// pool, global switch and recycle limits on the "Home folders" tab of /settings.
/// </summary>
public class HomeFoldersPageTests
{
    [Fact]
    public void SearchDestination_ForHomeFolderOverview_RequiresManageHomesOnly()
    {
        var destination = Assert.Single(SearchDestinations.All, d => d.Url == "/home-folders");

        Assert.Equal(ManagementPermission.ManageHomes, destination.RequiredAny);
        Assert.DoesNotContain(SearchDestinations.All, d => d.Url.StartsWith("/home-folders?tab="));
    }

    [Fact]
    public void SearchDestination_ForHomeFolderSettings_OpensTheSettingsTab()
    {
        var destination = Assert.Single(SearchDestinations.All, d => d.Url == "/settings?tab=homefolders");

        Assert.Equal(ManagementPermission.ManageSystemSettings, destination.RequiredAny);
    }

    [Theory]
    [InlineData("homefolders")]
    [InlineData("HomeFolders")]
    public void SettingsTab_HomeFoldersDeepLink_IsRecognized(string query)
    {
        Assert.True(Settings.TryParseTab(query, out var tab));
        Assert.Equal(SettingsTab.HomeFolders, tab);
    }
}

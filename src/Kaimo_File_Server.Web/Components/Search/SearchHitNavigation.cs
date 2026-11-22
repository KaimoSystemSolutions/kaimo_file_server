using Kaimo_File_Server.Infrastructure.Services.Files;
using Kaimo_File_Server.Search;
using Kaimo_File_Server.Web.Services.Files;
using Microsoft.AspNetCore.Components;

namespace Kaimo_File_Server.Web.Components.Search;

/// <summary>
/// Opens a search hit in the file browser (shared by the search dropdown and the results page):
/// a folder is entered, a file's folder opens with the file selected.
/// </summary>
public static class SearchHitNavigation
{
    /// <summary>Route segment of the own home folder (/files/user).</summary>
    public const string HomeRoute = "user";

    /// <summary>
    /// Presents a home-folder hit the way the browser shows the home: as "user", relative to
    /// the caller's own folder, so the folder GUID never reaches the UI or the URL. Hits in other
    /// users' homes (only visible with AccessHomeContents) keep the "users" share.
    /// </summary>
    public static FileDocument ToWebHit(FileDocument hit, Guid userId)
    {
        if (!hit.ShareName.Equals(HomeDirectoryService.ShareName, StringComparison.OrdinalIgnoreCase))
            return hit;

        var root = HomeDirectoryService.HomePathOf(userId) + "/";
        var path = hit.SharePath.Replace('\\', '/').TrimStart('/');
        if (!path.StartsWith(root, StringComparison.OrdinalIgnoreCase))
            return hit;

        hit.ShareName = HomeRoute;
        hit.SharePath = path[root.Length..];
        return hit;
    }

    /// <summary>Share-relative folder that contains <paramref name="sharePath"/>.</summary>
    public static string FolderOf(string sharePath)
        => (Path.GetDirectoryName(sharePath.Replace('\\', '/')) ?? "").Replace('\\', '/').Trim('/');

    /// <summary>The browser URL of a folder hit, or of the folder holding a file hit.</summary>
    public static string BrowserUrl(string routeBase, string sharePath, bool isDirectory)
    {
        var folder = isDirectory ? sharePath.Replace('\\', '/').Trim('/') : FolderOf(sharePath);
        return string.IsNullOrEmpty(folder) || folder == "." ? routeBase : $"{routeBase}/{folder}";
    }

    /// <summary>
    /// Navigates to the hit. <paramref name="routeBase"/> / <paramref name="browserShare"/> default
    /// to the local-share browser; a virtual share passes its own route and id.
    /// </summary>
    public static void Open(NavigationManager nav, FileSelectionCoordinator selection,
        string share, string sharePath, string name, bool isDirectory,
        string? routeBase = null, string? browserShare = null)
    {
        routeBase ??= $"/files/{share}";
        var url = BrowserUrl(routeBase, sharePath, isDirectory);

        if (!isDirectory)
        {
            // The home browser addresses its share by the real name, not by the "user" route.
            browserShare ??= share == HomeRoute ? HomeDirectoryService.ShareName : share;
            // The open browser selects the file in place; otherwise the request waits for it.
            if (selection.RequestSelection(browserShare, FolderOf(sharePath), name))
                return;
        }

        nav.NavigateTo(url);
    }
}

using Kaimo_File_Server.Core.Domain;
using Kaimo_File_Server.Core.Security.Management;

namespace Kaimo_File_Server.Web.Components.ViewModels;

/// <summary>
/// The home-folder share browsed as a whole (<c>/files/users</c>) by holders of
/// <see cref="ManagementPermission.AccessHomeContents"/>. Which homes are reachable is decided by
/// the ACL service; this part only gates the route and maps the GUID-named home folders to their
/// owner's username: shown in the listing and the breadcrumb, and accepted in the path (typed
/// address or URL) in place of the GUID. The real path, and thus the editable path bar, keeps
/// the GUID.
/// </summary>
public partial class FileBrowserViewModel
{
    // Home folder name (owner GUID) → owner username.
    private Dictionary<string, string> _homeOwnerNames = new(StringComparer.OrdinalIgnoreCase);

    // The whole home-folder share in the regular browser: its top-level folders are homes.
    private bool InHomesShare => CurrentShare is { IsUserHomes: true } && !IsHomeBrowser;

    /// <summary>Whether the signed-in user may open the whole home-folder share.</summary>
    public async Task<bool> CanBrowseAllHomesAsync()
        => await GetCurrentUserContextAsync() is { } actor
           && await _mgmtAuth.HasAnyPermissionAsync(actor, ManagementPermission.AccessHomeContents);

    /// <summary>
    /// Name shown for an entry: the owner's username for a home folder at the root of the
    /// home-folder share, the real name everywhere else.
    /// </summary>
    public string DisplayNameOf(FileMetadata entry)
        => entry.IsDirectory && InHomesShare && CurrentPath.Length == 0
            ? HomeLabel(entry.Name)
            : entry.Name;

    // Owner username of a top-level home folder; the name itself when no user owns it.
    private string HomeLabel(string folderName)
        => _homeOwnerNames.GetValueOrDefault(folderName) ?? folderName;

    /// <summary>
    /// Loads the owner names (always at the share root, so new users show up; elsewhere only
    /// once) and swaps a username in the first path segment for that user's home folder. A path
    /// whose first segment names no user is left as it is.
    /// </summary>
    private async Task<string> ResolveHomeOwnerSegmentAsync(string subPath)
    {
        if (!InHomesShare)
            return subPath;
        if (subPath.Length == 0 || _homeOwnerNames.Count == 0)
            _homeOwnerNames = (await _userRepo.GetAllAsync())
                .ToDictionary(u => u.Id.ToString("D"), u => u.Username, StringComparer.OrdinalIgnoreCase);

        var slash = subPath.IndexOf('/');
        var first = slash < 0 ? subPath : subPath[..slash];
        if (first.Length == 0 || _homeOwnerNames.ContainsKey(first))
            return subPath;
        var owner = _homeOwnerNames.FirstOrDefault(
            p => string.Equals(p.Value, first, StringComparison.OrdinalIgnoreCase)).Key;
        return owner is null ? subPath : owner + subPath[first.Length..];
    }
}

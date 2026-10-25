using Kaimo_File_Server.Core.Domain;
using Kaimo_File_Server.Core.Security;

namespace Kaimo_File_Server.Web.Components.ViewModels;

/// <summary>
/// The home-folder share browsed as a whole (<c>/files/users</c>) by holders of
/// <see cref="ManagementPermission.AccessHomeContents"/>. Which homes are reachable is decided by
/// the ACL service; this part only gates the route and labels the GUID-named home folders.
/// </summary>
public partial class FileBrowserViewModel
{
    private Dictionary<string, string> _homeOwnerNames = new(StringComparer.OrdinalIgnoreCase);

    // The share root of the home-folder share in the regular browser: every child is a home.
    private bool IsAtHomesRoot
        => CurrentShare is { IsUserHomes: true } && !IsHomeBrowser && CurrentPath.Length == 0;

    /// <summary>Whether the signed-in user may open the whole home-folder share.</summary>
    public async Task<bool> CanBrowseAllHomesAsync()
        => await GetCurrentUserContextAsync() is { } actor
           && await _mgmtAuth.HasAnyPermissionAsync(actor, ManagementPermission.AccessHomeContents);

    /// <summary>
    /// Username of the owner of a home folder at the root of the home-folder share, shown as a
    /// note next to the folder's real (GUID) name; <c>null</c> everywhere else.
    /// </summary>
    public string? GetHomeOwnerName(FileMetadata entry)
        => entry.IsDirectory && IsAtHomesRoot ? _homeOwnerNames.GetValueOrDefault(entry.Name) : null;

    private async Task LoadHomeOwnerNamesAsync()
    {
        if (!IsAtHomesRoot)
            return;
        _homeOwnerNames = (await _userRepo.GetAllAsync())
            .ToDictionary(u => u.Id.ToString("D"), u => u.Username, StringComparer.OrdinalIgnoreCase);
    }
}

using Kaimo_File_Server.Core.Helpers;
using Kaimo_File_Server.Web.Components.Search;

namespace Kaimo_File_Server.Web.Components.ViewModels;

public sealed partial class ExternalStorageSyncViewModel
{
    // ══════════════════════════════════════════
    //  Home folders shown by username (local folder of a sync)
    // ══════════════════════════════════════════

    /// <summary>Owner usernames of the home folders; loaded only when the home-folder share is visible.</summary>
    private HomeLabels _homeLabels = HomeLabels.None;

    /// <summary>
    /// The local folder of a sync as shown, rooted ("/" for the share root): on the home-folder
    /// share its first segment (a home named by its owner's GUID) as the owner's username, like
    /// the file browser. The stored path keeps the GUID.
    /// </summary>
    public string DisplayLocalPath(Guid localShareId, string? sharePath)
        => "/" + ShownLocalPath(localShareId, ShareRelativePath.Normalize(sharePath));

    // The share-relative local folder as shown (unrooted, like the stored path): used for the
    // Running Jobs entry of a manual run, whose summary shows the stored path's format.
    private string ShownLocalPath(Guid localShareId, string sharePath)
        => IsHomesShare(localShareId) ? _homeLabels.DisplayPath(sharePath) : sharePath;

    /// <summary>
    /// The stored (share-relative) path for a typed one: on the home-folder share a username in
    /// the first segment is swapped for that user's home folder, as the file browser accepts it.
    /// A path whose first segment already is a home folder, or names no user, is only normalized.
    /// </summary>
    public string ResolveLocalPath(Guid localShareId, string? shownPath)
    {
        var normalized = ShareRelativePath.Normalize(shownPath);
        return IsHomesShare(localShareId) && normalized.Length > 0
            ? ShareRelativePath.Normalize(_homeLabels.ResolvePath(normalized))
            : normalized;
    }

    private bool IsHomesShare(Guid shareId) => Shares.Any(share => share.Id == shareId && share.IsUserHomes);

    private Task LoadHomeLabelsAsync()
        => Shares.Any(share => share.IsUserHomes) ? ReloadHomeLabelsAsync() : Task.CompletedTask;

    private async Task ReloadHomeLabelsAsync() => _homeLabels = HomeLabels.FromUsers(await users.GetAllAsync());
}

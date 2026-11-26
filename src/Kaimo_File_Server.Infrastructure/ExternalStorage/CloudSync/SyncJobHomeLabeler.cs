using Kaimo_File_Server.Core.Repositories.Identity;
using Kaimo_File_Server.Core.Repositories.Shares;

namespace Kaimo_File_Server.Infrastructure.ExternalStorage.CloudSync;

/// <summary>
/// Names the local folder of a scheduled sync job the way the file browser does: on the
/// home-folder share the home (a folder named by its owner's GUID) as the owner's username.
/// Only the Running Jobs text changes; the job itself keeps the stored path. Lookups are cached
/// for the lifetime of the instance (one scheduler pass).
/// </summary>
internal sealed class SyncJobHomeLabeler(IShareRepository? shares, IUserRepository? users)
{
    private readonly Dictionary<Guid, bool> _homesShares = [];
    private readonly Dictionary<Guid, string?> _ownerNames = [];

    /// <summary>
    /// The share-relative local folder as shown. A path outside the home-folder share, or whose
    /// first segment names no existing user, is returned unchanged.
    /// </summary>
    public async Task<string> ShownLocalPathAsync(Guid shareId, string localPath)
    {
        if (shares is null || users is null)
            return localPath;

        var path = localPath.Replace('\\', '/').Trim('/');
        var slash = path.IndexOf('/');
        var home = slash < 0 ? path : path[..slash];
        if (!Guid.TryParseExact(home, "D", out var ownerId))
            return localPath;

        try
        {
            if (!await IsHomesShareAsync(shareId))
                return localPath;
            var owner = await OwnerNameAsync(ownerId);
            return owner is null ? localPath : owner + path[home.Length..];
        }
        catch (Exception)
        {
            // The label is cosmetic: a failed lookup must never keep a due sync from running.
            return localPath;
        }
    }

    private async Task<bool> IsHomesShareAsync(Guid shareId)
    {
        if (!_homesShares.TryGetValue(shareId, out var isHomes))
            _homesShares[shareId] = isHomes = (await shares!.GetByIdAsync(shareId))?.IsUserHomes == true;
        return isHomes;
    }

    private async Task<string?> OwnerNameAsync(Guid ownerId)
    {
        if (!_ownerNames.TryGetValue(ownerId, out var name))
            _ownerNames[ownerId] = name = (await users!.GetByIdAsync(ownerId))?.Username;
        return name;
    }
}

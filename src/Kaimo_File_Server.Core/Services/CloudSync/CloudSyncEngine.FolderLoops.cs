using System.Collections.Concurrent;
using Kaimo_File_Server.Core.Helpers;

namespace Kaimo_File_Server.Core.Services.CloudSync;

// Remote folders that link back to an ancestor (see FolderLoop): found by the pre-scan, which
// stops there, and skipped by reconciliation in both directions. Pulling one would copy the
// ancestor again on every level; pushing into one would write into the ancestor itself.
internal sealed partial class CloudSyncEngine
{
    // Remote path of each loop folder → the ancestor it repeats.
    private readonly ConcurrentDictionary<string, string> _remoteLoops = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Records <paramref name="dir"/> as a loop when its listing repeats an ancestor's. Ancestors
    /// are looked up in the pre-scan cache, which holds every ancestor before its subfolders are listed.
    /// </summary>
    private bool IsRemoteLoop(string dir, IReadOnlyList<CloudItemMeta> items)
    {
        for (int cut = dir.LastIndexOf('/'); cut >= 0; cut = cut == 0 ? -1 : dir.LastIndexOf('/', cut - 1))
        {
            string ancestor = dir[..cut];
            if (!_remoteListings.TryGetValue(ancestor, out var listing))
                return false;
            if (FolderLoop.Repeats(items, listing, item => (item.Name, item.IsDirectory, item.Size, item.ModifiedAt)))
            {
                _remoteLoops[dir] = ancestor;
                return true;
            }
        }
        return false;
    }

    private bool IsBelowRemoteLoop(string path)
        => _remoteLoops.Keys.Any(loop => path.StartsWith(loop + "/", StringComparison.OrdinalIgnoreCase));

    /// <summary>Skips a loop folder in reconciliation and reports it, so the gap is visible.</summary>
    private bool SkipRemoteLoop(string remoteChild, string localChild)
    {
        if (!_remoteLoops.TryGetValue(remoteChild, out string? ancestor))
            return false;
        _failures?.Add(new SyncFailure(localChild, SyncFailureOperation.List,
            $"Skipped: the folder repeats the content of '{ancestor}' (a symbolic link loop on the remote server)."));
        return true;
    }
}

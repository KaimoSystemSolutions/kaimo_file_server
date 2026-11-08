using Kaimo_File_Server.Core.Domain;
using Kaimo_File_Server.Core.Helpers;
using Kaimo_File_Server.Web.Services;

namespace Kaimo_File_Server.Web.Components.ViewModels;

/// <summary>
/// Name search below a folder of the loaded virtual share, used by the global search bar.
/// Virtual shares are not indexed, so this walks the remote tree through the shared directory cache.
/// </summary>
public sealed partial class RemoteCloudAccessFileBrowserViewModel
{
    /// <summary>
    /// Folders and files below <paramref name="fromPath"/> whose name contains
    /// <paramref name="query"/>, shallow ones first. When <paramref name="budget"/> runs out the
    /// hits found so far are returned; cancelling <paramref name="cancellationToken"/> throws.
    /// Only the share the browser currently shows is searched (its access was checked on load).
    /// </summary>
    public async Task<List<FileMetadata>> SearchNamesAsync(
        Guid shareId, string fromPath, string query, int maxHits, TimeSpan budget, CancellationToken cancellationToken)
    {
        if (_shownTarget is not { } pin || pin.Share.Id != shareId
            || !ShareRelativePath.TryNormalizeStrict(fromPath ?? string.Empty, out var start))
            return [];

        return await FileNameWalk.SearchAsync(
            start, query, maxHits, budget,
            folder => ListCoreAsync(pin, folder),
            (exception, folder) => _logger.LogDebug(exception, "Name search skipped virtual share folder {Path}", folder),
            cancellationToken);
    }
}

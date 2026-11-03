using Kaimo_File_Server.Core.Domain;
using Kaimo_File_Server.Core.Helpers;

namespace Kaimo_File_Server.Web.Components.ViewModels;

/// <summary>
/// Name search below a folder of the loaded virtual share, used by the global search bar.
/// Virtual shares are not indexed, so this walks the remote tree breadth-first through the
/// shared directory cache and matches names only (case-insensitive substring).
/// </summary>
public sealed partial class RemoteCloudAccessFileBrowserViewModel
{
    // ponytail: fixed folder cap per search; a remote search index if huge trees need full coverage
    private const int MaxSearchedFolders = 2000;

    /// <summary>
    /// Folders and files below <paramref name="fromPath"/> whose name contains
    /// <paramref name="query"/>, shallow ones first. When <paramref name="budget"/> runs out the
    /// hits found so far are returned; cancelling <paramref name="cancellationToken"/> throws.
    /// Only the share the browser currently shows is searched (its access was checked on load).
    /// </summary>
    public async Task<List<FileMetadata>> SearchNamesAsync(
        Guid shareId, string fromPath, string query, int maxHits, TimeSpan budget, CancellationToken cancellationToken)
    {
        var hits = new List<FileMetadata>();
        if (_shownTarget is not { } pin || pin.Share.Id != shareId
            || !ShareRelativePath.TryNormalizeStrict(fromPath ?? string.Empty, out var start))
            return hits;

        using var walk = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        walk.CancelAfter(budget);
        var pending = new Queue<string>([start]);
        var searched = 0;
        try
        {
            while (pending.TryDequeue(out var folder) && searched++ < MaxSearchedFolders)
            {
                List<FileMetadata> items;
                try
                {
                    items = await ListCoreAsync(pin, folder).WaitAsync(walk.Token);
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    _logger.LogDebug(exception, "Name search skipped virtual share folder {Path}", folder);
                    continue;
                }

                foreach (var item in items)
                {
                    if (item.Name.Contains(query, StringComparison.OrdinalIgnoreCase))
                    {
                        hits.Add(item);
                        if (hits.Count >= maxHits) return hits;
                    }
                    if (item.IsDirectory) pending.Enqueue(item.Path);
                }
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // Budget spent: partial results are better than none on a slow provider.
        }
        return hits;
    }
}

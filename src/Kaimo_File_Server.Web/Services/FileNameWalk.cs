using Kaimo_File_Server.Core.Domain;

namespace Kaimo_File_Server.Web.Services;

/// <summary>
/// Name search over storage without a search index: walks a tree breadth-first and matches names
/// only (case-insensitive substring), so shallow hits come first.
/// </summary>
internal static class FileNameWalk
{
    // ponytail: fixed folder cap per search; a remote search index if huge trees need full coverage
    private const int MaxSearchedFolders = 2000;

    /// <summary>
    /// When <paramref name="budget"/> runs out the hits found so far are returned; cancelling
    /// <paramref name="cancellationToken"/> throws. A folder that cannot be listed is skipped.
    /// </summary>
    public static async Task<List<FileMetadata>> SearchAsync(
        string start, string query, int maxHits, TimeSpan budget,
        Func<string, Task<List<FileMetadata>>> list,
        Action<Exception, string>? onFolderSkipped,
        CancellationToken cancellationToken)
    {
        var hits = new List<FileMetadata>();
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
                    items = await list(folder).WaitAsync(walk.Token);
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    onFolderSkipped?.Invoke(exception, folder);
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

using Kaimo_File_Server.Core.Domain;

namespace Kaimo_File_Server.Core.Services.File;

/// <summary>
/// Plans how a rename moves version history onto a path that may already have one. Pure, so
/// the database repository and the in-memory test repository apply identical rules.
///
/// The destination's history is kept and merged, not discarded: safe-save editors
/// (LibreOffice, Word for Mac, many others) write the new content to a temporary file and
/// rename it over the document, and the document's history must survive that. A replaced
/// unrelated file stays recoverable the same way.
/// </summary>
public static class VersionHistoryMerge
{
    /// <param name="source">Rows at <paramref name="oldPath"/> or below it.</param>
    /// <param name="destination">Rows at <paramref name="newPath"/> or below it that are not in <paramref name="source"/>.</param>
    public static VersionRenamePlan Plan(
        string oldPath, string newPath,
        IReadOnlyCollection<FileVersion> source, IReadOnlyCollection<FileVersion> destination,
        DateTime nowUtc)
    {
        var newPaths = source.ToDictionary(
            v => v.Id,
            v => v.FilePath == oldPath ? newPath : newPath + v.FilePath.Substring(oldPath.Length));
        var displaced = new List<FileVersion>();
        var numbers = new Dictionary<Guid, int>();
        var copies = new List<FileVersion>();

        var destinationByPath = destination.ToLookup(v => v.FilePath, StringComparer.Ordinal);
        foreach (var moved in source.GroupBy(v => newPaths[v.Id], StringComparer.Ordinal))
        {
            var existing = destinationByPath[moved.Key].ToList();
            if (existing.Count == 0) continue;

            // One row per file and second (unique index): the moved row carries the live lineage.
            var movedSeconds = moved.Select(v => v.SnapshotTimestampUtc).ToHashSet();
            displaced.AddRange(existing.Where(v => movedSeconds.Contains(v.SnapshotTimestampUtc)));
            var kept = existing.Where(v => !movedSeconds.Contains(v.SnapshotTimestampUtc)).ToList();
            if (kept.Count == 0) continue;

            var merged = kept.Concat(moved).OrderBy(v => v.SnapshotTimestampUtc).ToList();
            for (var i = 0; i < merged.Count; i++)
                numbers[merged[i].Id] = i + 1;

            // Retention never removes the newest row because it mirrors the live content. When
            // the replaced file has newer rows, the moved content needs a newer row of its own.
            var live = moved.MaxBy(v => v.SnapshotTimestampUtc)!;
            var newest = merged[^1].SnapshotTimestampUtc;
            if (newest > live.SnapshotTimestampUtc)
            {
                var at = TruncateToSeconds(nowUtc);
                if (at <= newest) at = newest.AddSeconds(1);
                copies.Add(new FileVersion(
                    live.ShareId, moved.Key, at, live.StoragePath, live.ContentHash,
                    live.Size, live.CreatedBy, merged.Count + 1));
            }
        }

        return new VersionRenamePlan(newPaths, displaced, numbers, copies);
    }

    private static DateTime TruncateToSeconds(DateTime value)
        => new(value.Ticks - value.Ticks % TimeSpan.TicksPerSecond, DateTimeKind.Utc);
}

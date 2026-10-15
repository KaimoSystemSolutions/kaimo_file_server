using Kaimo_File_Server.Core.Repositories;

namespace Kaimo_File_Server.Core.Services.Sync;

/// <summary>Where a synced folder went: the first rename that moved it and its current path.</summary>
/// <param name="RenameId">Sequence of the first rename that moved the folder (or an ancestor).</param>
/// <param name="NewPath">The folder's path after following every later rename.</param>
/// <param name="RenamedAtUtc">When the last rename of the chain was logged.</param>
public sealed record SyncRootMove(long RenameId, string NewPath, DateTime RenamedAtUtc);

/// <summary>
/// Follows a device sync root through the change log. A client syncs a share subtree by path; when
/// that folder or one of its ancestors is renamed, the logged <c>Renamed</c> entries tell where it
/// went, so the client can re-point its connection instead of losing it.
/// </summary>
public static class SyncRootRelocation
{
    /// <summary>Upper bound of followed renames, so a ping-pong chain cannot loop a request.</summary>
    public const int MaxHops = 32;

    /// <summary>Maps <paramref name="path"/> through a rename of itself or an ancestor.</summary>
    public static string Rewrite(string path, string oldPath, string newPath)
        => path.Length == oldPath.Length ? newPath : newPath + path[oldPath.Length..];

    /// <summary>
    /// Resolves the rename chain of <paramref name="root"/> starting after <paramref name="afterSeq"/>:
    /// from the first covering rename (a client with a cursor), or with <paramref name="newest"/> from
    /// the latest one (no cursor). Returns <c>null</c> when no rename covers the path.
    /// </summary>
    public static async Task<SyncRootMove?> ResolveAsync(
        IFileChangeLogRepository log, Guid shareId, string root, long afterSeq, bool newest,
        CancellationToken ct = default)
    {
        var first = await log.FindRenameCoveringAsync(shareId, root, afterSeq, newest, ct);
        if (first is null)
            return null;

        var current = first;
        var path = Rewrite(root, first.OldPath!, first.Path);
        for (var hop = 1; hop < MaxHops; hop++)
        {
            var next = await log.FindRenameCoveringAsync(shareId, path, current.Seq, newest: false, ct);
            if (next is null)
                break;
            path = Rewrite(path, next.OldPath!, next.Path);
            current = next;
        }

        return new SyncRootMove(first.Seq, path, current.CreatedAtUtc);
    }

    /// <summary>
    /// For a client without a cursor, whose last view is unknown: the newest rename covering
    /// <paramref name="root"/> may be an ancestor's that came after the root itself moved (x/a → x/b,
    /// then x → y maps x/a to a missing y/a). Tries the covering renames from the newest back and
    /// returns the first chain <paramref name="accept"/> takes; <c>null</c> when none does.
    /// </summary>
    public static async Task<SyncRootMove?> ResolveNewestAcceptedAsync(
        IFileChangeLogRepository log, Guid shareId, string root, Func<SyncRootMove, Task<bool>> accept,
        CancellationToken ct = default)
    {
        long? before = null;
        for (var tried = 0; tried < MaxHops; tried++)
        {
            var candidate = await log.FindRenameCoveringAsync(shareId, root, 0, newest: true, ct, before);
            if (candidate is null)
                return null;

            var move = await ResolveAsync(log, shareId, root, candidate.Seq - 1, newest: false, ct);
            if (move is not null && await accept(move))
                return move;
            before = candidate.Seq;
        }
        return null;
    }
}

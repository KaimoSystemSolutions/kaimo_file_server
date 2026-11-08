namespace Kaimo_File_Server.Core.Helpers;

/// <summary>
/// Recognizes a remote folder that is a symbolic link back to one of its own ancestors. Servers
/// that follow such links (Samba, most WebDAV servers) report them as plain folders, so a walk
/// would list the same content again on every level until the server's path length limit.
/// Remote protocols expose no file identity to compare, so the folder's listing is compared with
/// its ancestors' instead: a loop lists exactly the ancestor's entries, with the same names,
/// sizes and times.
/// </summary>
public static class FolderLoop
{
    /// <summary>
    /// Whether <paramref name="listing"/> repeats <paramref name="ancestor"/> entry for entry.
    /// Only listings with at least two entries, one of them a folder, count: a loop always holds
    /// the folder that leads back to it, and a single entry is too weak a signal, since folders
    /// such as "p/p/p" made in one go each list the same single subfolder.
    /// </summary>
    // ponytail: a loop whose target holds nothing but the one folder leading back to it is not
    // recognized and runs to the server's path limit as before; compare a second level if that occurs.
    public static bool Repeats<T>(
        IReadOnlyCollection<T> listing,
        IReadOnlyCollection<T> ancestor,
        Func<T, (string Name, bool IsDirectory, long? Size, DateTime? Modified)> entry)
    {
        if (listing.Count < 2 || listing.Count != ancestor.Count || !listing.Any(item => entry(item).IsDirectory))
            return false;
        var entries = listing.Select(entry).ToHashSet();
        return ancestor.All(item => entries.Contains(entry(item)));
    }
}

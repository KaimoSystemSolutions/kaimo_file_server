namespace Kaimo_File_Server.Infrastructure.Backup.Restic;

/// <summary>
/// Local repository paths as the administrator sees them, and repository folders that can be
/// connected. Stored paths stay container paths; only what is displayed changes.
/// </summary>
public static class ResticLocalPaths
{
    /// <summary>
    /// How the backup folder mounted into the container (<c>Backup:RootPath</c>) is shown in the
    /// UI and the recovery kit: the folder the administrator mounted, not the container's path.
    /// </summary>
    public const string DisplayRoot = "/backups";

    /// <summary>True when <paramref name="path"/> is <paramref name="backupRoot"/> or lies below it.</summary>
    public static bool IsInBackupRoot(string path, string backupRoot) => IsBelow(path, backupRoot.TrimEnd('/'));

    /// <summary>Maps a path below <paramref name="backupRoot"/> to <see cref="DisplayRoot"/>; other paths stay as they are.</summary>
    public static string ToDisplay(string path, string backupRoot)
    {
        var root = backupRoot.TrimEnd('/');
        return IsBelow(path, root) ? DisplayRoot + path[root.Length..] : path;
    }

    /// <summary>
    /// Reverse of <see cref="ToDisplay"/>: a typed <c>/backups/…</c> path becomes the container path.
    /// A local root that itself starts with <c>/backups</c> is shadowed by this; such a root has to
    /// be the mounted backup folder (<c>Backup:RootPath=/backups</c>) to stay reachable.
    /// </summary>
    public static string FromDisplay(string path, string backupRoot)
        => IsBelow(path, DisplayRoot) ? backupRoot.TrimEnd('/') + path[DisplayRoot.Length..] : path;

    private static bool IsBelow(string path, string root)
        => root.Length > 0 && path.StartsWith(root, StringComparison.Ordinal)
           && (path.Length == root.Length || path[root.Length] == '/');

    /// <summary>
    /// Folders below <paramref name="roots"/> (up to <paramref name="maxDepth"/> levels) that hold
    /// a restic repository (a <c>config</c> file and a <c>keys</c> folder), sorted by path.
    /// A repository is not searched further; unreadable folders are skipped. Repositories matched by
    /// <paramref name="skip"/> (e.g. already connected ones) are left out and do not count against
    /// <paramref name="maxResults"/>.
    /// </summary>
    public static IReadOnlyList<string> FindRepositories(
        IEnumerable<string> roots, int maxDepth = 3, int maxResults = 20, Func<string, bool>? skip = null)
    {
        var found = new List<string>();
        foreach (var root in roots)
            Search(Path.TrimEndingDirectorySeparator(root), 0);
        return found.Order(StringComparer.Ordinal).Take(maxResults).ToList();

        void Search(string folder, int depth)
        {
            // ponytail: plain depth-first walk; fine for a backup folder, a large mounted tree would want a time budget.
            if (depth >= maxDepth || found.Count >= maxResults)
                return;
            IEnumerable<string> children;
            try { children = Directory.EnumerateDirectories(folder).Order(StringComparer.Ordinal).ToList(); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return; }
            foreach (var child in children)
            {
                if (found.Count >= maxResults)
                    return;
                if (File.Exists(Path.Combine(child, "config")) && Directory.Exists(Path.Combine(child, "keys")))
                {
                    if (skip?.Invoke(child) != true)
                        found.Add(child);
                }
                else
                    Search(child, depth + 1);
            }
        }
    }
}

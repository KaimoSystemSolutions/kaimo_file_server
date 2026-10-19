using System.Security.Cryptography;
using System.Text;
using Kaimo_File_Server.Core.Domain;
using Kaimo_File_Server.Core.Domain.Backup;
using Kaimo_File_Server.Core.Helpers;
using Kaimo_File_Server.Core.Language;
using Kaimo_File_Server.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Kaimo_File_Server.Infrastructure.Backup.Restic;

public partial class BackupJobExecutor
{
    /// <summary>
    /// Stored as the name of the system source in run outcomes; the UI shows it in its own
    /// language (runs execute without one).
    /// </summary>
    public const string SystemSourceKey = "kaimo:system";

    /// <summary>Display name of the system source in <paramref name="culture"/> (default: the thread's UI culture).</summary>
    public static string SystemSourceName(System.Globalization.CultureInfo? culture = null)
        => Resources.ResourceManager.GetString("Web_Backup_Source_System", culture) ?? "Database & system";

    /// <summary>Display name of a pool source ("Pool pool01").</summary>
    public static string PoolSourceName(string poolName, System.Globalization.CultureInfo? culture = null)
        => string.Format(culture ?? System.Globalization.CultureInfo.CurrentCulture,
            Resources.ResourceManager.GetString("Web_Backup_Source_Pool", culture) ?? "Pool {0}", poolName);

    /// <summary>The pool's folder name, usable as a restic tag value (no comma: restic splits tag lists on it).</summary>
    public static string PoolTag(string pool)
        => Path.GetFileName(Path.TrimEndingDirectorySeparator(pool)).Replace(',', '_');

    /// <summary>Stable per pool path, so the manifest staging path (part of the snapshot paths) never changes.</summary>
    private static string PathHash(string path)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Path.TrimEndingDirectorySeparator(path))))[..16].ToLowerInvariant();

    public static bool IsInside(string path, string folder)
    {
        var p = Path.TrimEndingDirectorySeparator(path).Replace('\\', '/');
        var f = Path.TrimEndingDirectorySeparator(folder).Replace('\\', '/');
        return p.StartsWith(f + "/", StringComparison.Ordinal);
    }

    /// <summary>Version blobs are useless without their database rows, so they need a database level.</summary>
    public static bool IncludesVersions(BackupJob job)
        => job.IncludeVersions && job.ContentLevel != BackupContentLevel.Files;

    /// <summary>
    /// Excludes of a pool: its own internal entries (<c>.kaimo-*</c>: version store, space
    /// reservations, ...), the internal folders of every folder in it and the excludes of each
    /// of its shares (recycle bins, home folders). The version store is re-included last
    /// (restic negated pattern) when wanted.
    /// </summary>
    internal static List<string> BuildPoolExcludes(string pool, IEnumerable<ShareDefinition> shares, bool includeRecycleBin, bool includeVersions)
    {
        var root = Path.TrimEndingDirectorySeparator(pool).Replace('\\', '/');
        var excludes = new List<string>
        {
            root + "/" + ShareEntryPolicy.InternalNamespacePrefix + "*",
            root + "/*/" + ShareEntryPolicy.InternalNamespacePrefix + "*",
        };
        foreach (var share in shares)
            excludes.AddRange(BuildShareExcludes(share, includeRecycleBin).Where(e => !excludes.Contains(e)));
        if (includeVersions)
            excludes.Add("!" + root + "/" + ResticClient.EscapePattern(Services.PoolVersionStorageLocator.FolderName));
        return excludes;
    }

    /// <summary>
    /// Excludes Kaimo-internal folders (always) and recycle bins (unless included), anchored
    /// at the share root (for the homes share additionally at each home folder) so user
    /// folders deeper in the tree that merely share the name are still backed up.
    /// </summary>
    internal static List<string> BuildShareExcludes(ShareDefinition share, bool includeRecycleBin)
    {
        var root = Path.TrimEndingDirectorySeparator(share.Path).Replace('\\', '/');
        var bases = share.IsUserHomes ? new[] { root, root + "/*" } : [root];
        var excludes = new List<string>();
        foreach (var b in bases)
        {
            excludes.Add(b + "/" + ShareEntryPolicy.InternalNamespacePrefix + "*");
            if (!includeRecycleBin)
                excludes.Add(b + "/" + ShareEntryPolicy.RecycleBinName);
        }
        return excludes;
    }

    /// <summary>
    /// Refuses to back up a missing source or a source that suddenly holds no file although
    /// earlier snapshots had content — the classic sign of an unmounted pool, which would
    /// otherwise silently produce an "everything deleted" snapshot that retention then uses to
    /// push out the good ones.
    /// </summary>
    internal static void GuardSource(string path, bool hadContentBefore)
    {
        if (!Directory.Exists(path))
            throw new ResticException("source_unavailable", "The share folder does not exist (pool not mounted?).");
        if (hadContentBefore && !HasUserFile(path))
            throw new ResticException("source_empty", "The source holds no file although earlier backups had content (pool not mounted?).");
    }

    /// <summary>
    /// True when a file exists anywhere below <paramref name="root"/> outside Kaimo-internal
    /// folders. Folders do not count: on an unmounted pool Kaimo itself recreates share folders,
    /// their <c>.kaimo-*</c> folders and the home folders, but never files. Stops at the first file.
    /// </summary>
    internal static bool HasUserFile(string root)
    {
        var options = new EnumerationOptions { IgnoreInaccessible = true, AttributesToSkip = 0 };
        var pending = new Stack<DirectoryInfo>([new DirectoryInfo(root)]);
        while (pending.TryPop(out var directory))
        {
            foreach (var entry in directory.EnumerateFileSystemInfos("*", options))
            {
                if (entry.Name.StartsWith(ShareEntryPolicy.InternalNamespacePrefix, StringComparison.Ordinal))
                    continue;
                if (entry is FileInfo)
                    return true;
                if (!entry.Attributes.HasFlag(FileAttributes.ReparsePoint))
                    pending.Push((DirectoryInfo)entry);
            }
        }
        return false;
    }

    private static Task<bool> HadContentBeforeAsync(ApplicationDbContext db, Guid repositoryId, Guid shareId, CancellationToken ct)
        => db.BackupSnapshots.AnyAsync(s => s.RepositoryId == repositoryId && s.ShareId == shareId && s.FileCount > 1, ct);

    private void EnsureLocalFreeSpace(BackupRepository repository)
    {
        if (repository.Backend != BackupBackend.Local || repository.MinFreeSpaceGb <= 0)
            return;
        var path = ResticRepositorySettings.Parse(repository.SettingsJson).Path;
        if (string.IsNullOrEmpty(path) || !Directory.Exists(path))
            throw new ResticException("repo_not_found", "The local repository folder does not exist.");
        long free;
        try { free = new DriveInfo(path).AvailableFreeSpace; }
        catch (Exception ex) when (ex is IOException or ArgumentException or UnauthorizedAccessException) { return; }
        if (free < repository.MinFreeSpaceGb * 1024L * 1024 * 1024)
            throw new ResticException("insufficient_space", "The repository target has less free space than configured.");
    }
}

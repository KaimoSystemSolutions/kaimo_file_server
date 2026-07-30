using System.Globalization;

namespace Kaimo_File_Server.SmbBridge.Services;

/// <summary>
/// Shared constants and path helpers for the snapshot materialization cache used
/// by <see cref="SnapshotGrpcService"/> and
/// <see cref="SnapshotCacheCleanupService"/>.
///
/// The cache root is global and MUST be outside every client-visible share. Its
/// layout is
/// <c>&lt;cache-root&gt;/&lt;share-id&gt;/&lt;@GMT&gt;/&lt;user-id&gt;/…</c>.
/// Using immutable ids prevents share-name and user-name collisions while keeping
/// every path returned to the VFS relative to the configured cache root.
///
/// Eviction cannot key off cached file mtimes because materialization deliberately
/// stamps historical modification times. Each @GMT token directory therefore has
/// a marker recording its real materialization time.
/// </summary>
internal static class SnapshotCache
{
    /// <summary>Old in-share namespace, reserved while legacy caches are removed.</summary>
    internal const string LegacyDirName = ".kaimo-snapshots";

    /// <summary>Default global cache root shared by the bridge and Samba containers.</summary>
    internal const string DefaultRoot = "/data/kaimo-system/.kaimo-snapshots";

    /// <summary>Per-@GMT-token marker file recording real materialization time (UTC).</summary>
    internal const string MarkerName = ".kaimo-cached-at";

    /// <summary>Cross-process advisory lock held by bridge and Samba readers.</summary>
    internal const string LeaseFileName = ".kaimo-lease";

    internal static string ConfiguredRoot(IConfiguration config)
    {
        string configured = config["Snapshots:Cache:RootPath"] ?? DefaultRoot;
        if (string.IsNullOrWhiteSpace(configured) || !Path.IsPathRooted(configured))
            throw new InvalidOperationException(
                "Snapshots:Cache:RootPath must be an absolute path outside every SMB share.");
        return Path.GetFullPath(configured);
    }

    /// <summary>Absolute root of one share's isolated cache projection.</summary>
    internal static string ShareRootFor(string cacheRoot, Guid shareId) =>
        Path.Combine(Path.GetFullPath(cacheRoot), shareId.ToString("N"));

    /// <summary>Cache-root-relative prefix returned to the native VFS.</summary>
    internal static string RelativeShareRootFor(Guid shareId) =>
        shareId.ToString("N");

    internal static string TokenRootFor(
        string cacheRoot, string shareScope, string gmtToken) =>
        Path.Combine(Path.GetFullPath(cacheRoot), shareScope, gmtToken);

    internal static string EnsureTokenRoot(
        string cacheRoot, Guid shareId, string gmtToken)
    {
        string root = Path.GetFullPath(cacheRoot);
        EnsureDirectory(root);
        string shareRoot = ShareRootFor(root, shareId);
        EnsureDirectory(shareRoot);
        string tokenRoot = Path.Combine(shareRoot, gmtToken);
        EnsureDirectory(tokenRoot);
        return tokenRoot;
    }

    internal static string EnsureLeaseFile(string tokenRoot)
    {
        EnsureDirectory(tokenRoot);
        string path = Path.Combine(tokenRoot, LeaseFileName);
        using (new FileStream(
                   path, FileMode.OpenOrCreate, FileAccess.Write,
                   FileShare.ReadWrite | FileShare.Delete))
        {
        }
        SetReadOnlyProjectionMode(path);
        return path;
    }

    /// <summary>Creates each security boundary in a user projection as 0750.</summary>
    internal static string EnsureUserScope(
        string cacheRoot, Guid shareId, string gmtToken, string userScope)
    {
        string tokenRoot = EnsureTokenRoot(cacheRoot, shareId, gmtToken);
        string userRoot = Path.Combine(tokenRoot, userScope);
        EnsureDirectory(userRoot);
        return userRoot;
    }

    /// <summary>
    /// Fails closed if the configured cache and a client-visible share overlap in
    /// either direction. This prevents an unusual/root-level share definition from
    /// accidentally publishing the otherwise global internal cache.
    /// </summary>
    internal static void EnsureIsolatedFromShare(string cacheRoot, string sharePath)
    {
        string cache = Path.GetFullPath(cacheRoot);
        string share = Path.GetFullPath(sharePath);
        if (IsSameOrDescendant(cache, share) || IsSameOrDescendant(share, cache))
            throw new InvalidOperationException(
                $"Snapshot cache '{cache}' overlaps SMB share '{share}'.");
    }

    /// <summary>Create a 0750 cache directory for bridge owner + Samba storage group.</summary>
    internal static void EnsureDirectory(string path)
    {
        Directory.CreateDirectory(path);
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(path,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
                UnixFileMode.GroupRead | UnixFileMode.GroupExecute);
        }
    }

    /// <summary>
    /// Ensures the marker for a token exists. Best-effort: a missing marker only
    /// makes the evictor fall back to filesystem timestamps.
    /// </summary>
    internal static void TouchMarker(string cacheRoot, Guid shareId, string gmtToken)
    {
        try
        {
            EnsureDirectory(cacheRoot);
            string shareRoot = ShareRootFor(cacheRoot, shareId);
            EnsureDirectory(shareRoot);
            string dir = Path.Combine(shareRoot, gmtToken);
            EnsureDirectory(dir);
            string marker = Path.Combine(dir, MarkerName);
            if (!File.Exists(marker))
            {
                File.WriteAllText(marker,
                    DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture));
                SetReadOnlyProjectionMode(marker);
            }
        }
        catch
        {
            // Non-fatal: eviction falls back to directory timestamps.
        }
    }

    internal static void SetReadOnlyProjectionMode(string path)
    {
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(path,
                UnixFileMode.UserRead | UnixFileMode.UserWrite |
                UnixFileMode.GroupRead);
        }
    }

    /// <summary>
    /// Reads the recorded materialization time of a token directory. Falls back to
    /// directory creation time when the marker is absent or unreadable.
    /// </summary>
    internal static DateTime CachedAtUtc(string gmtTokenDir)
    {
        try
        {
            string marker = Path.Combine(gmtTokenDir, MarkerName);
            if (File.Exists(marker))
            {
                string text = File.ReadAllText(marker).Trim();
                if (DateTime.TryParse(text, CultureInfo.InvariantCulture,
                        DateTimeStyles.RoundtripKind, out var stamped))
                    return stamped.ToUniversalTime();
            }
        }
        catch
        {
            // Fall through to the filesystem timestamp.
        }

        try
        {
            return Directory.GetCreationTimeUtc(gmtTokenDir);
        }
        catch
        {
            return DateTime.UtcNow;
        }
    }

    private static bool IsSameOrDescendant(string parent, string candidate)
    {
        StringComparison comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        if (string.Equals(parent, candidate, comparison))
            return true;

        string prefix = parent.EndsWith(Path.DirectorySeparatorChar)
            ? parent
            : parent + Path.DirectorySeparatorChar;
        return candidate.StartsWith(prefix, comparison);
    }
}

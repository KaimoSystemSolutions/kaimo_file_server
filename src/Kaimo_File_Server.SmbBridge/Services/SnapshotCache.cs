using System.Globalization;

namespace Kaimo_File_Server.SmbBridge.Services;

/// <summary>
/// Shared constants and helpers for the in-share snapshot materialization cache
/// (<c>&lt;share&gt;/.kaimo-snapshots/&lt;@GMT&gt;/…</c>) used by
/// <see cref="SnapshotGrpcService"/> (writer) and
/// <see cref="SnapshotCacheCleanupService"/> (evictor).
///
/// Eviction cannot key off the cached files' mtimes: materialization deliberately
/// stamps the <em>historical</em> modification time (so Windows "Previous Versions"
/// can tell versions apart), which may be years in the past. Instead, each @GMT
/// token directory carries a <see cref="MarkerName"/> file whose contents record the
/// real wall-clock UTC time the version was materialized — that is the basis for
/// TTL/size eviction. The marker name is <c>.kaimo-</c>-prefixed so the VFS
/// <c>readdir</c> filter already hides it from SMB listings.
/// </summary>
internal static class SnapshotCache
{
    /// <summary>Hidden per-share directory holding materialized (decompressed) versions.</summary>
    internal const string DirName = ".kaimo-snapshots";

    /// <summary>Per-@GMT-token marker file recording the real materialization time (UTC).</summary>
    internal const string MarkerName = ".kaimo-cached-at";

    /// <summary>Absolute path of a share's snapshot cache root.</summary>
    internal static string RootFor(string sharePath) => Path.Combine(sharePath, DirName);

    /// <summary>
    /// Ensures the marker file for a given @GMT token directory exists, stamping it
    /// with the current UTC time on first creation. Best-effort: never throws (a
    /// missing marker only makes the evictor fall back to filesystem timestamps).
    /// </summary>
    internal static void TouchMarker(string sharePath, string gmtToken)
    {
        try
        {
            string dir = Path.Combine(RootFor(sharePath), gmtToken);
            Directory.CreateDirectory(dir);
            string marker = Path.Combine(dir, MarkerName);
            if (!File.Exists(marker))
                File.WriteAllText(marker, DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture));
        }
        catch
        {
            // Non-fatal: eviction falls back to directory timestamps.
        }
    }

    /// <summary>
    /// Reads the recorded materialization time of a @GMT token directory. Falls back
    /// to the directory's creation time, then its last-write time, if the marker is
    /// absent or unreadable.
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
            // fall through to filesystem timestamps
        }

        try
        {
            // Creation time ≈ when we first materialized this token (we only ever
            // set the *write* time to the historical snapshot value, never creation,
            // so LastWriteTime must NOT be used as the cache-age basis here). If the
            // filesystem reports a bogus/epoch creation time this over-retains the
            // entry (safe direction); the marker file fixes it on the next resolve.
            return Directory.GetCreationTimeUtc(gmtTokenDir);
        }
        catch
        {
            return DateTime.UtcNow;
        }
    }
}

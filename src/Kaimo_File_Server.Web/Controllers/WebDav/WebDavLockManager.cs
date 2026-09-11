using System.Collections.Concurrent;

namespace Kaimo_File_Server.Web.Controllers.WebDav;

/// <summary>
/// In-memory table of WebDAV exclusive write locks (RFC 4918 class 2), keyed by
/// share id plus share-relative path. Windows Explorer, Finder and Office all
/// refuse to write reliably to a class-1 server, so a lock table is required in
/// practice — but only the exclusive write lock they actually take is supported;
/// shared locks are advertised as unsupported.
///
/// Ceiling: the table is per-process and in-memory. It does not survive a Web
/// restart and does not coordinate with SMB locks held by <c>smbd</c> in another
/// process — exactly the situation that already exists between SMB and the web UI.
/// Cross-protocol locking would need a shared lock authority behind the bridge and
/// is out of scope. Expired entries are swept lazily on access.
/// </summary>
public sealed class WebDavLockManager
{
    /// <summary>A held lock as stored in the table.</summary>
    public sealed record LockInfo(string Token, string Path, bool DepthInfinity, string? OwnerRaw, DateTimeOffset ExpiryUtc);

    private readonly ConcurrentDictionary<string, LockInfo> _locks = new();
    private readonly TimeProvider _clock;

    public WebDavLockManager(TimeProvider clock) => _clock = clock;

    private static string Key(Guid shareId, string path) => $"{shareId}:{path}";

    /// <summary>
    /// Acquires an exclusive write lock, or returns <c>null</c> when a conflicting
    /// live lock already covers the path (a lock on the path itself, an infinity
    /// lock on an ancestor, or — for an infinity request — any lock on a descendant).
    /// </summary>
    public LockInfo? TryAcquire(Guid shareId, string path, bool depthInfinity, string? ownerRaw, int timeoutSeconds)
    {
        var now = _clock.GetUtcNow();
        SweepExpired(shareId, now);

        foreach (var existing in LiveLocksForShare(shareId, now))
        {
            if (Conflicts(existing, path, depthInfinity))
                return null;
        }

        var info = new LockInfo(
            $"opaquelocktoken:{Guid.NewGuid()}", path, depthInfinity, ownerRaw,
            now.AddSeconds(timeoutSeconds));
        _locks[Key(shareId, path)] = info;
        return info;
    }

    /// <summary>Extends a lock's lifetime when the supplied token matches; returns the refreshed lock or <c>null</c>.</summary>
    public LockInfo? Refresh(Guid shareId, string path, string token, int timeoutSeconds)
    {
        var now = _clock.GetUtcNow();
        if (_locks.TryGetValue(Key(shareId, path), out var info) &&
            info.Token == token && info.ExpiryUtc > now)
        {
            var refreshed = info with { ExpiryUtc = now.AddSeconds(timeoutSeconds) };
            _locks[Key(shareId, path)] = refreshed;
            return refreshed;
        }
        return null;
    }

    /// <summary>Releases a lock when the token matches. Returns false for an unknown path or a wrong token.</summary>
    public bool Unlock(Guid shareId, string path, string token)
    {
        var key = Key(shareId, path);
        if (_locks.TryGetValue(key, out var info) && info.Token == token)
            return _locks.TryRemove(key, out _);
        return false;
    }

    /// <summary>The live locks recorded exactly at <paramref name="path"/> (for <c>lockdiscovery</c>).</summary>
    public IReadOnlyList<LockInfo> GetLocksAt(Guid shareId, string path)
    {
        var now = _clock.GetUtcNow();
        return _locks.TryGetValue(Key(shareId, path), out var info) && info.ExpiryUtc > now
            ? [info]
            : [];
    }

    /// <summary>
    /// Whether a mutating request on <paramref name="path"/> is blocked by a lock
    /// the caller does not hold. A request is allowed when every covering live lock's
    /// token is present in <paramref name="providedTokens"/>.
    /// </summary>
    public bool IsBlocked(Guid shareId, string path, IReadOnlyCollection<string> providedTokens)
    {
        var now = _clock.GetUtcNow();
        foreach (var existing in LiveLocksForShare(shareId, now))
        {
            if (Covers(existing, path) && !providedTokens.Contains(existing.Token))
                return true;
        }
        return false;
    }

    /// <summary>Remaining lifetime of a lock in whole seconds (never negative).</summary>
    public int RemainingSeconds(LockInfo info)
        => Math.Max(0, (int)(info.ExpiryUtc - _clock.GetUtcNow()).TotalSeconds);

    private IEnumerable<LockInfo> LiveLocksForShare(Guid shareId, DateTimeOffset now)
    {
        var prefix = shareId + ":";
        foreach (var (key, info) in _locks)
        {
            if (key.StartsWith(prefix, StringComparison.Ordinal) && info.ExpiryUtc > now)
                yield return info;
        }
    }

    private void SweepExpired(Guid shareId, DateTimeOffset now)
    {
        var prefix = shareId + ":";
        foreach (var (key, info) in _locks)
        {
            if (key.StartsWith(prefix, StringComparison.Ordinal) && info.ExpiryUtc <= now)
                _locks.TryRemove(key, out _);
        }
    }

    private static bool Conflicts(LockInfo existing, string newPath, bool newDepthInfinity)
        => Covers(existing, newPath)
           || (newDepthInfinity && IsSameOrDescendant(newPath, existing.Path));

    private static bool Covers(LockInfo existing, string targetPath)
        => existing.Path == targetPath
           || (existing.DepthInfinity && IsSameOrDescendant(existing.Path, targetPath));

    /// <summary>True when <paramref name="candidate"/> is <paramref name="ancestor"/> itself or nested beneath it.</summary>
    private static bool IsSameOrDescendant(string ancestor, string candidate)
    {
        if (ancestor == candidate) return true;
        if (ancestor.Length == 0) return true; // share root covers everything
        return candidate.StartsWith(ancestor + "/", StringComparison.Ordinal);
    }
}

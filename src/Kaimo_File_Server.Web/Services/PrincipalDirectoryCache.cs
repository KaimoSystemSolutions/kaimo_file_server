using Kaimo_File_Server.Core.Domain.Identity;
using Kaimo_File_Server.Core.Repositories.Identity;

namespace Kaimo_File_Server.Web.Services;

/// <summary>
/// Process-wide, stale-while-revalidate cache of all users, groups and roles for principal pickers.
/// <see cref="GetAsync"/> returns the cached snapshot instantly (loading only when cold);
/// <see cref="RevalidateAsync"/> reloads in the background and swaps the snapshot only when its
/// content changed, so callers can detect a change by reference. Concurrent reloads share one
/// query, and a reload is skipped while the snapshot is younger than the minimum interval.
/// It holds no authorization: callers expose the data only after their own permission check.
/// </summary>
public sealed class PrincipalDirectoryCache
{
    private readonly Func<Task<PrincipalDirectory>> _load;
    private readonly long _minIntervalMs;
    private readonly object _gate = new();
    private volatile PrincipalDirectory? _current;
    private Task? _inflight;
    private long _loadedAtMs;

    public PrincipalDirectoryCache(Func<Task<PrincipalDirectory>> load, TimeSpan minRevalidateInterval)
    {
        _load = load;
        _minIntervalMs = (long)minRevalidateInterval.TotalMilliseconds;
    }

    /// <summary>The cached snapshot, or null before the first successful load.</summary>
    public PrincipalDirectory? Current => _current;

    /// <summary>The cached snapshot; loads (or joins a running load) only when the cache is cold.</summary>
    public async Task<PrincipalDirectory> GetAsync()
    {
        if (_current is { } cached) return cached;
        await RevalidateAsync();
        return _current!;
    }

    /// <summary>
    /// Reloads unless a reload is already running (joined) or the snapshot is still fresh.
    /// A failed reload throws and leaves the current snapshot untouched.
    /// </summary>
    public Task RevalidateAsync()
    {
        lock (_gate)
        {
            if (_inflight is { IsCompleted: false }) return _inflight;
            if (_current is not null && Environment.TickCount64 - _loadedAtMs < _minIntervalMs)
                return Task.CompletedTask;
            return _inflight = ReloadAsync();
        }
    }

    /// <summary>
    /// Revalidates and returns the current snapshot when it differs from <paramref name="seen"/>
    /// (the one the caller shows); null when nothing changed or the reload failed (logged, the
    /// caller keeps its list).
    /// </summary>
    public async Task<PrincipalDirectory?> RevalidateSinceAsync(PrincipalDirectory seen, ILogger logger)
    {
        try
        {
            await RevalidateAsync();
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Revalidating the principal list failed; keeping the cached list");
            return null;
        }
        var current = _current;
        return current is null || ReferenceEquals(current, seen) ? null : current;
    }

    private async Task ReloadAsync()
    {
        var fresh = await _load();
        lock (_gate)
        {
            if (_current is null || !_current.HasSameContentAs(fresh))
                _current = fresh;
            _loadedAtMs = Environment.TickCount64;
        }
    }

    /// <summary>Loads all principals as sorted summaries; users via the slim database projection.</summary>
    public static async Task<PrincipalDirectory> LoadAsync(
        IUserRepository users, IGroupRepository groups, IRoleRepository roles)
    {
        var userList = await users.GetSummariesAsync();
        var groupList = (await groups.GetAllAsync()).Select(g => new PrincipalSummary(g.Id, g.Name));
        var roleList = (await roles.GetAllAsync()).Select(r => new PrincipalSummary(r.Id, r.Name));
        return new PrincipalDirectory(Sorted(userList), Sorted(groupList), Sorted(roleList));
    }

    // A stable order keeps unchanged data from looking like a change between reloads. Invariant
    // culture (not ordinal) sorts umlauts next to their base letter; the request culture is
    // unknown in a process-wide singleton.
    private static IReadOnlyList<PrincipalSummary> Sorted(IEnumerable<PrincipalSummary> items) =>
        items.OrderBy(p => p.Name, StringComparer.InvariantCultureIgnoreCase).ThenBy(p => p.Id).ToList();
}

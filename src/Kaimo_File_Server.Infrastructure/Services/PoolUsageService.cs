using System.Collections.Concurrent;

namespace Kaimo_File_Server.Infrastructure.Services;

/// <summary>
/// Process-wide cache for <see cref="PoolUsageScanner"/> results. A pool scan walks every file,
/// so at most one runs per pool at a time (concurrent callers share it) and the last result is
/// kept until the next explicit scan.
/// </summary>
public sealed class PoolUsageService
{
    private readonly Func<string, string?, PoolUsageBreakdown> _scan;
    private readonly ConcurrentDictionary<string, PoolUsageBreakdown> _cache = new();
    private readonly Dictionary<string, Task<PoolUsageBreakdown>> _running = new();
    private readonly object _gate = new();

    public PoolUsageService() : this(PoolUsageScanner.Scan) { }

    internal PoolUsageService(Func<string, string?, PoolUsageBreakdown> scan) => _scan = scan;

    /// <summary>The last completed scan of the pool, or <c>null</c> if it was never scanned.</summary>
    public PoolUsageBreakdown? GetCached(string poolPath)
        => _cache.TryGetValue(poolPath, out var breakdown) ? breakdown : null;

    /// <summary>Scans the pool in the background, joining a scan of it that is already running.</summary>
    public Task<PoolUsageBreakdown> ScanAsync(string poolPath, string? homesSharePath)
    {
        lock (_gate)
        {
            if (_running.TryGetValue(poolPath, out var running))
                return running;

            // The finally needs _gate, so it cannot remove the entry before it is added below.
            var scan = Task.Run(() =>
            {
                try
                {
                    var result = _scan(poolPath, homesSharePath);
                    _cache[poolPath] = result;
                    return result;
                }
                finally
                {
                    lock (_gate) _running.Remove(poolPath);
                }
            });
            _running[poolPath] = scan;
            return scan;
        }
    }
}

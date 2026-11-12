using System.Collections.Concurrent;
using Kaimo_File_Server.Core.Helpers;
using Kaimo_File_Server.Infrastructure.Services.Versioning;

namespace Kaimo_File_Server.Infrastructure.Services.Pools;

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

/// <summary>What Kaimo itself stores in one storage pool, split by origin.</summary>
public sealed record PoolUsageBreakdown(
    long FileBytes,
    long RecycleBinBytes,
    long VersionBytes,
    long OtherServerBytes,
    DateTime ScannedAtUtc)
{
    public long KaimoBytes => FileBytes + RecycleBinBytes + VersionBytes + OtherServerBytes;
}

/// <summary>
/// Walks a storage pool once and attributes every file to a category: share files, recycle
/// bins (<c>&lt;share&gt;/.RECYCLE_BIN</c> and, in the home-folder share,
/// <c>&lt;uid&gt;/.RECYCLE_BIN</c>), the version store and other server data (dot folders such
/// as <c>.kaimo-moving-*</c>, <c>lost+found</c>, loose files in the pool root). Links are never
/// followed, unreadable folders are skipped.
/// </summary>
public static class PoolUsageScanner
{
    // ponytail: rounds every file up to 4 KiB to approximate allocated blocks (statvfs counts
    // blocks, FileInfo.Length does not). Off on compressed/ZFS filesystems; read st_blocks via
    // stat() if that ever matters.
    internal const long AllocationUnit = 4096;

    private static readonly EnumerationOptions TopLevel = new()
    {
        AttributesToSkip = FileAttributes.ReparsePoint,
        IgnoreInaccessible = true,
    };

    private static readonly EnumerationOptions Recursive = new()
    {
        RecurseSubdirectories = true,
        AttributesToSkip = FileAttributes.ReparsePoint,
        IgnoreInaccessible = true,
    };

    private static readonly string RecycleBinPrefix = ShareEntryPolicy.RecycleBinName + Path.DirectorySeparatorChar;

    public static PoolUsageBreakdown Scan(string poolPath, string? homesSharePath)
    {
        long files = 0, recycleBins = 0, versions = 0, other = 0;
        var pool = new DirectoryInfo(poolPath);
        var homes = homesSharePath is null ? null : Normalize(homesSharePath);

        if (pool.Exists)
        {
            foreach (var entry in pool.EnumerateFileSystemInfos("*", TopLevel))
            {
                if (entry is not DirectoryInfo dir)
                    other += Allocated(((FileInfo)entry).Length);
                else if (dir.Name == PoolVersionStorageLocator.FolderName)
                    versions += SizeOf(dir);
                else if (dir.Name.StartsWith('.') || dir.Name == "lost+found")
                    other += SizeOf(dir);
                else
                {
                    // Homes keep one bin per user one level deeper (ShareDefinition.RecycleRootDepth).
                    var binDepth = homes is not null && Normalize(dir.FullName) == homes ? 1 : 0;
                    var start = dir.FullName.Length + 1;
                    foreach (var file in dir.EnumerateFiles("*", Recursive))
                    {
                        if (IsInRecycleBin(file.FullName.AsSpan(start), binDepth))
                            recycleBins += Allocated(file.Length);
                        else
                            files += Allocated(file.Length);
                    }
                }
            }
        }

        return new PoolUsageBreakdown(files, recycleBins, versions, other, DateTime.UtcNow);
    }

    private static bool IsInRecycleBin(ReadOnlySpan<char> shareRelative, int depth)
    {
        for (var i = 0; i < depth; i++)
        {
            var separator = shareRelative.IndexOf(Path.DirectorySeparatorChar);
            if (separator < 0) return false;
            shareRelative = shareRelative[(separator + 1)..];
        }
        return shareRelative.StartsWith(RecycleBinPrefix, StringComparison.Ordinal);
    }

    private static long SizeOf(DirectoryInfo dir)
        => dir.EnumerateFiles("*", Recursive).Sum(file => Allocated(file.Length));

    private static long Allocated(long length)
        => (length + AllocationUnit - 1) / AllocationUnit * AllocationUnit;

    private static string Normalize(string path)
        => Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
}

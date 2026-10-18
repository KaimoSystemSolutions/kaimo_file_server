using Kaimo_File_Server.Core.Helpers;

namespace Kaimo_File_Server.Infrastructure.Services;

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

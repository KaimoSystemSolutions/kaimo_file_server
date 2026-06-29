using Kaimo_File_Server.Core.Domain;
using Smb.FileSystem;

namespace Kaimo_File_Server.Smb;

/// <summary>
/// Maps Kaimo <see cref="FileMetadata"/> to the library's <see cref="FileEntryInfo"/>
/// (used for CREATE/QUERY_INFO/QUERY_DIRECTORY responses). Times are FILETIME (100ns since 1601 UTC).
/// </summary>
internal static class KaimoFileInfo
{
    /// <summary>Info for a single open handle; <paramref name="size"/> is the live size from the session.</summary>
    public static FileEntryInfo From(FileMetadata meta, long size) => Build(
        meta.Name, meta.IsDirectory, meta.IsDirectory ? 0 : size,
        meta.CreatedAt, meta.ModifiedAt, meta.LastAccessedAt ?? meta.ModifiedAt,
        IdSource(meta));

    /// <summary>Info for a directory-listing entry (size comes from the metadata).</summary>
    public static FileEntryInfo FromListing(FileMetadata meta) => Build(
        meta.Name, meta.IsDirectory, meta.IsDirectory ? 0 : meta.Size,
        meta.CreatedAt, meta.ModifiedAt, meta.LastAccessedAt ?? meta.ModifiedAt,
        IdSource(meta));

    /// <summary>The synthetic "." / ".." entries — timestamps are ignored by clients.</summary>
    public static FileEntryInfo Pseudo(string name)
    {
        long now = DateTime.UtcNow.ToFileTimeUtc();
        return Build(name, isDirectory: true, size: 0, default, default, default, name, now);
    }

    /// <summary>Best-effort info when metadata can't be read (e.g. right after CREATE).</summary>
    public static FileEntryInfo Synthesize(string name, bool isDirectory, long size)
    {
        long now = DateTime.UtcNow.ToFileTimeUtc();
        return Build(name, isDirectory, isDirectory ? 0 : size, default, default, default, name, now);
    }

    private static FileEntryInfo Build(
        string name, bool isDirectory, long size,
        DateTime created, DateTime modified, DateTime accessed, string idSource, long? fixedTime = null)
        => new()
        {
            Name = name,
            Attributes = isDirectory ? SmbFileAttributes.Directory : SmbFileAttributes.Normal,
            EndOfFile = size,
            AllocationSize = Align(size),
            CreationTime = fixedTime ?? ToFileTime(created),
            LastWriteTime = fixedTime ?? ToFileTime(modified),
            LastAccessTime = fixedTime ?? ToFileTime(accessed),
            ChangeTime = fixedTime ?? ToFileTime(modified),
            IndexNumber = StableId(idSource),
        };

    private static string IdSource(FileMetadata meta)
        => string.IsNullOrEmpty(meta.Path) ? meta.Name : meta.Path;

    private static long Align(long size) => size <= 0 ? 0 : (size + 4095) / 4096 * 4096;

    private static long ToFileTime(DateTime dt)
    {
        try { return (dt.Kind == DateTimeKind.Utc ? dt : dt.ToUniversalTime()).ToFileTimeUtc(); }
        catch { return 0; }
    }

    /// <summary>Stable, process-independent 64-bit id (FNV-1a) for FileId/IndexNumber.</summary>
    private static long StableId(string s)
    {
        const ulong basis = 14695981039346656037UL, prime = 1099511628211UL;
        ulong h = basis;
        foreach (char c in s) { h ^= c; h *= prime; }
        return (long)(h & 0x7FFFFFFFFFFFFFFFUL);
    }
}

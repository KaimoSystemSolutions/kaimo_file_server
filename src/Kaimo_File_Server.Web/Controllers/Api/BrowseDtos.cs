using Kaimo_File_Server.Core.Domain;

namespace Kaimo_File_Server.Web.Controllers.Api;

/// <summary>A share the caller may access.</summary>
public sealed record ShareDto(Guid Id, string Name, bool IsRecycleEnabled);

/// <summary>A file or directory entry in a listing.</summary>
public sealed record FileEntryDto(
    string Path,
    string Name,
    bool IsDirectory,
    long Size,
    DateTime CreatedAtUtc,
    DateTime ModifiedAtUtc)
{
    public static FileEntryDto From(FileMetadata m) => new(
        m.Path, m.Name, m.IsDirectory, m.Size, m.CreatedAt, m.ModifiedAt);
}

/// <summary>Source and destination for a rename/move.</summary>
public sealed record RenameRequest(string From, string To);

/// <summary>One stored version of a file.</summary>
public sealed record FileVersionDto(DateTime SnapshotTimestampUtc, long Size, string ContentHash);

namespace Kaimo_File_Server.Infrastructure.Clouds;

public sealed record OneDriveItemMeta(
    string ProviderId,
    string Name,
    string Path,
    bool IsDirectory,
    long Size,
    DateTime CreatedAtUtc,
    DateTime ModifiedAtUtc,
    string? ETag);
public sealed record OneDriveFolderReference(string ProviderId, string Path);

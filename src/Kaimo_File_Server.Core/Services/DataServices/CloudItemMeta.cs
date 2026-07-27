namespace Kaimo_File_Server.Infrastructure.Clouds;

public record CloudItemMeta(
    bool IsDirectory,
    string Name,
    string Path,
    long Size,
    DateTime ModifiedAt
);

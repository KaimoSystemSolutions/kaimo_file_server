namespace Kaimo_File_Server.Infrastructure.Clouds;

/// <param name="CreatedAt">
/// The remote creation time, or <c>null</c> when the provider does not report one
/// (smbclient, SFTP, rsync); a pull then uses <paramref name="ModifiedAt"/> instead.
/// </param>
public record CloudItemMeta(
    bool IsDirectory,
    string Name,
    string Path,
    long Size,
    DateTime ModifiedAt,
    DateTime? CreatedAt = null
);

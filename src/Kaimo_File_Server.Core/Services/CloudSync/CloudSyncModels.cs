namespace Kaimo_File_Server.Core.Services.CloudSync;

/// <summary>The transfer step that failed for a single item during a sync run.</summary>
public enum SyncFailureOperation
{
    Upload,
    Download,
    Delete,
    List,
    CreateDirectory
}

/// <summary>
/// One item that could not be synchronized. Collected instead of aborting the
/// whole run so a single unreadable or rejected file does not stop a large
/// directory sync; the run reports every failure at the end.
/// </summary>
public sealed record SyncFailure(
    string Path,
    SyncFailureOperation Operation,
    string Reason);

/// <summary>
/// Provider-neutral account information that may be shown in management UIs.
/// Providers may omit either value when their API does not expose it.
/// </summary>
public sealed record CloudAccountInfo(string? DisplayName, string? Email, string? PictureUrl);

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

/// <summary>Which side of a sync a directory-missing failure came from.</summary>
public enum SyncEndpoint
{
    Local,
    Remote
}

public enum SyncMode
{
    Pull,
    Push,
    TwoWay
}

/// <summary>
/// A sync endpoint directory no longer exists. Carries which side failed so the
/// health surface can show an explicit "local folder removed" / "remote folder
/// removed" message instead of a generic sync failure.
/// </summary>
public sealed class SyncDirectoryMissingException(SyncEndpoint endpoint, Exception? innerException = null)
    : Exception(
        endpoint == SyncEndpoint.Local
            ? "The local sync directory no longer exists."
            : "The remote sync directory no longer exists.",
        innerException)
{
    public SyncEndpoint Endpoint { get; } = endpoint;
}

/// <summary>
/// Raised when a namespace mutation overlaps a currently active cloud sync.
/// </summary>
public sealed class CloudSyncOperationConflictException(string message)
    : IOException(message);

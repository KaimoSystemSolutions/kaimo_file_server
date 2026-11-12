namespace Kaimo_File_Server.Core.Services.ExternalStorage;

/// <summary>
/// Operations a storage provider can actually perform. Consumers must check
/// these flags before presenting or invoking optional functionality.
/// </summary>
[Flags]
public enum StorageProviderCapabilities
{
    None = 0,
    Browse = 1 << 0,
    Read = 1 << 1,
    Write = 1 << 2,
    CreateDirectory = 1 << 3,
    Delete = 1 << 4,
    Rename = 1 << 5,
    Move = 1 << 6,
    ServerSideCopy = 1 << 7,
    Sync = 1 << 8,
    StableItemIds = 1 << 9,
    WatchChanges = 1 << 10,
    DelegatedAuthorization = 1 << 11,
    ApplicationAuthorization = 1 << 12,
    RequiresHostMount = 1 << 13,
    OptimizedSync = 1 << 14,
    /// <summary>The provider exposes its own remote file-store session without a preconfigured host mount.</summary>
    DirectFileAccess = 1 << 15
}

public enum StorageConnectionHealthState
{
    Healthy,
    Degraded,
    Unavailable,
    InvalidConfiguration,
    IdentityMismatch
}

/// <summary>Sanitized provider health result safe for persistence and UI display.</summary>
public sealed record StorageConnectionHealthResult(
    StorageConnectionHealthState State,
    string Code,
    DateTime CheckedAtUtc)
{
    public bool IsHealthy => State == StorageConnectionHealthState.Healthy;
}

/// <summary>Provider-neutral metadata for one remote child item.</summary>
public sealed record RemoteStorageItem(
    string Name,
    string Path,
    bool IsDirectory,
    long? Size,
    DateTime? ModifiedAtUtc,
    string? StableId = null,
    DateTime? CreatedAtUtc = null);

/// <summary>A validated remote directory that can be persisted by a virtual share or sync.</summary>
public sealed record StorageDirectoryTarget(string Path, string? StableId = null);

public enum OptimizedSyncDirection
{
    Pull,
    Push
}

public sealed record OptimizedSyncRequest(
    string LocalRootPath,
    string LocalRelativePath,
    string RemotePath,
    OptimizedSyncDirection Direction,
    bool DeleteExtraneousFiles = false,
    IReadOnlyCollection<string>? ExcludedExtensions = null,
    long? MaximumFileSizeBytes = null,
    long? MaximumTransferBytesPerSecond = null);

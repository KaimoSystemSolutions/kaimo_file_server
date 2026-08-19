using Kaimo_File_Server.Core.Domain;

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
    string? StableId = null);

/// <summary>A validated remote directory that can be persisted by a virtual share or sync.</summary>
public sealed record StorageDirectoryTarget(string Path, string? StableId = null);

/// <summary>The authenticated remote system rejected an operation.</summary>
public sealed class RemoteStorageAccessDeniedException(string message, Exception? innerException = null)
    : IOException(message, innerException);

/// <summary>
/// Provider-neutral boundary for selecting and validating durable remote roots.
/// Consumers do not need to know whether a provider identifies directories by
/// path, share/export name, or a stable provider item ID.
/// </summary>
public interface IStorageDirectoryTargetResolver
{
    Task<IReadOnlyList<RemoteStorageItem>> ListDirectoriesAsync(
        StorageConnection connection,
        string path,
        CancellationToken cancellationToken = default);

    Task<StorageDirectoryTarget> ResolveDirectoryAsync(
        StorageConnection connection,
        string path,
        CancellationToken cancellationToken = default);
}

/// <summary>Optional file-store operations exposed by browse-capable providers.</summary>
public interface IRemoteFileStore
{
    Task<IReadOnlyList<RemoteStorageItem>> ListAsync(
        string path,
        CancellationToken cancellationToken = default);

    Task<Stream> OpenReadAsync(string path, CancellationToken cancellationToken = default);

    Task WriteAsync(
        string path,
        Stream content,
        bool overwrite,
        CancellationToken cancellationToken = default);

    Task CreateDirectoryAsync(string path, CancellationToken cancellationToken = default);
    Task DeleteAsync(string path, bool recursive, CancellationToken cancellationToken = default);
    Task MoveAsync(string sourcePath, string destinationPath, CancellationToken cancellationToken = default);
}

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

/// <summary>Optional native sync transport, for example rsync over pinned SSH.</summary>
public interface IOptimizedStorageSync
{
    Task SynchronizeAsync(
        OptimizedSyncRequest request,
        CancellationToken cancellationToken = default);
}

/// <summary>A live provider session exposes only operations declared by its capabilities.</summary>
public interface IStorageSession : IAsyncDisposable
{
    Guid ConnectionId { get; }
    StorageProviderCapabilities Capabilities { get; }
    IRemoteFileStore? RemoteFiles { get; }
    IOptimizedStorageSync? OptimizedSync { get; }
}

/// <summary>
/// Lifecycle boundary implemented once per provider. Generic UI and runtime
/// code resolve providers by ID and never branch on provider-specific names.
/// </summary>
public interface IStorageConnectionProvider
{
    string Id { get; }
    string DisplayName { get; }
    StorageProviderCapabilities Capabilities { get; }
    IReadOnlySet<StorageAuthorizationMode> AuthorizationModes { get; }

    Task<IStorageSession> OpenSessionAsync(
        StorageConnection connection,
        CancellationToken cancellationToken = default);

    Task<StorageConnectionHealthResult> TestAsync(
        StorageConnection connection,
        CancellationToken cancellationToken = default);

    Task RevokeAsync(
        StorageConnection connection,
        CancellationToken cancellationToken = default);
}

public interface IStorageConnectionProviderCatalog
{
    IReadOnlyCollection<IStorageConnectionProvider> Providers { get; }
    bool TryGet(string providerId, out IStorageConnectionProvider provider);
    IStorageConnectionProvider GetRequired(string providerId);
}

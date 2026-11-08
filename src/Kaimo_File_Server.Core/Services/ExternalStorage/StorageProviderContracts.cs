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
    string? StableId = null,
    DateTime? CreatedAtUtc = null);

/// <summary>A validated remote directory that can be persisted by a virtual share or sync.</summary>
public sealed record StorageDirectoryTarget(string Path, string? StableId = null);

/// <summary>
/// The provider behind a virtual share cannot be reached right now (connection not ready,
/// authentication or transport failure, server error). Transports report a retryable 503.
/// </summary>
public sealed class RemoteShareUnavailableException(string message, Exception? innerException = null)
    : IOException(message, innerException);

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

    /// <summary>How many <see cref="ListAsync"/> calls a sync may run at once (default: one).</summary>
    int ListConcurrency => 1;

    /// <summary>
    /// Optional: lists every folder below <paramref name="path"/> in one call, keyed by the folder
    /// path relative to it ("" = the folder itself, "a/b" = nested). Leave out any folder that could
    /// not be listed completely. <c>null</c> = not supported (the default).
    /// </summary>
    Task<IReadOnlyDictionary<string, IReadOnlyList<RemoteStorageItem>>?> TryListTreeAsync(
        string path,
        CancellationToken cancellationToken = default)
        => Task.FromResult<IReadOnlyDictionary<string, IReadOnlyList<RemoteStorageItem>>?>(null);

    Task<Stream> OpenReadAsync(string path, CancellationToken cancellationToken = default);

    Task WriteAsync(
        string path,
        Stream content,
        bool overwrite,
        CancellationToken cancellationToken = default);

    Task CreateDirectoryAsync(string path, CancellationToken cancellationToken = default);
    Task DeleteAsync(string path, bool recursive, CancellationToken cancellationToken = default);
    Task MoveAsync(string sourcePath, string destinationPath, CancellationToken cancellationToken = default);

    /// <summary>
    /// Best-effort: stamps a file's modification time after a write so a sync keeps the
    /// source mtime. Providers that cannot set it keep this no-op default; the next sync
    /// then sees the upload time instead.
    /// </summary>
    Task TrySetModifiedTimeAsync(string path, DateTime modifiedUtc, CancellationToken cancellationToken = default)
        => Task.CompletedTask;

    /// <summary>
    /// Same for a directory, once a sync wrote its content. Defaults to the file variant;
    /// providers that address directories differently (WebDAV collections) override it.
    /// </summary>
    Task TrySetDirectoryModifiedTimeAsync(string path, DateTime modifiedUtc, CancellationToken cancellationToken = default)
        => TrySetModifiedTimeAsync(path, modifiedUtc, cancellationToken);

    /// <summary>
    /// Best-effort: stamps the modification time and, when <paramref name="createdUtc"/> is set,
    /// the creation time. Providers without settable creation times keep this default.
    /// </summary>
    Task TrySetTimesAsync(
        string path, DateTime modifiedUtc, DateTime? createdUtc, CancellationToken cancellationToken = default)
        => TrySetModifiedTimeAsync(path, modifiedUtc, cancellationToken);

    /// <summary>Directory variant of <see cref="TrySetTimesAsync"/>.</summary>
    Task TrySetDirectoryTimesAsync(
        string path, DateTime modifiedUtc, DateTime? createdUtc, CancellationToken cancellationToken = default)
        => TrySetDirectoryModifiedTimeAsync(path, modifiedUtc, cancellationToken);
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
    /// <summary>
    /// Runs the native transfer and returns the absolute filesystem paths of the
    /// regular files that were newly written into the local tree (pull only) so
    /// the caller can hand exactly those to the search index without a full-share
    /// rescan. A push, or a transport that cannot itemize its changes, returns an
    /// empty list.
    /// </summary>
    Task<IReadOnlyList<string>> SynchronizeAsync(
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

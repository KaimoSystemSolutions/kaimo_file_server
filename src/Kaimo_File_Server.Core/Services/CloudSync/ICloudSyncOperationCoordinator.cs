namespace Kaimo_File_Server.Core.Services.CloudSync;

/// <summary>
/// Coordinates cloud-sync runs with namespace mutations independently of how a
/// sync was started (UI, timer, or a future background worker).
/// </summary>
public interface ICloudSyncOperationCoordinator
{
    Task<ICloudSyncOperationLease?> TryBeginSyncAsync(
        Guid shareId,
        string localPath,
        CancellationToken cancellationToken = default);

    Task<ICloudSyncOperationLease?> TryBeginPathMutationAsync(
        Guid shareId,
        string oldPath,
        string newPath,
        CancellationToken cancellationToken = default);

    Task<ICloudSyncOperationLease?> TryBeginShareMutationAsync(
        Guid shareId,
        CancellationToken cancellationToken = default);

    Task<bool> TryReserveExternalPathMutationAsync(
        Guid shareId,
        string oldPath,
        string newPath,
        TimeSpan lifetime,
        CancellationToken cancellationToken = default);

    Task CompleteExternalPathMutationAsync(
        Guid shareId,
        string oldPath,
        string newPath,
        CancellationToken cancellationToken = default);
}

public interface ICloudSyncOperationLease : IAsyncDisposable
{
    /// <summary>
    /// Cancelled once the lease ends while the holder is still working: shortly before it
    /// reaches its absolute lifetime cap, or after it was lost to another process (it expired
    /// and was taken over). Cooperative holders stop instead of overlapping with another
    /// owner. Leases that cannot end this way never cancel it.
    /// </summary>
    CancellationToken LeaseLost => CancellationToken.None;
}

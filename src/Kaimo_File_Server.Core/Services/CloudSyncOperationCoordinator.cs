using Kaimo_File_Server.Core.Helpers;

namespace Kaimo_File_Server.Core.Services;

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

public interface ICloudSyncOperationLease : IAsyncDisposable;

/// <summary>
/// Raised when a namespace mutation overlaps a currently active cloud sync.
/// </summary>
public sealed class CloudSyncOperationConflictException(string message)
    : IOException(message);

/// <summary>
/// In-process coordinator used by tests and single-process hosts. Operations
/// carry canonical paths for diagnostics, while exclusivity is share-wide.
/// </summary>
public sealed class InMemoryCloudSyncOperationCoordinator(TimeProvider timeProvider)
    : ICloudSyncOperationCoordinator
{
    private readonly object _gate = new();
    private readonly Dictionary<Guid, OperationEntry> _operations = [];
    private readonly Dictionary<ExternalMutationKey, ExternalReservation>
        _externalReservations = [];

    public Task<ICloudSyncOperationLease?> TryBeginSyncAsync(
        Guid shareId,
        string localPath,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        string path = ShareRelativePath.Normalize(localPath);
        lock (_gate)
        {
            PurgeExpiredReservations();
            if (_operations.Values.Any(entry => entry.ShareId == shareId) ||
                _externalReservations.Values.Any(entry => entry.ShareId == shareId))
            {
                return Task.FromResult<ICloudSyncOperationLease?>(null);
            }

            return Task.FromResult<ICloudSyncOperationLease?>(AddLease(
                new OperationEntry(
                    Guid.NewGuid(), shareId, OperationKind.Sync, path, null)));
        }
    }

    public Task<ICloudSyncOperationLease?> TryBeginPathMutationAsync(
        Guid shareId,
        string oldPath,
        string newPath,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        string oldNormalized = ShareRelativePath.Normalize(oldPath);
        string newNormalized = ShareRelativePath.Normalize(newPath);
        lock (_gate)
        {
            PurgeExpiredReservations();
            if (_operations.Values.Any(entry => entry.ShareId == shareId) ||
                _externalReservations.Values.Any(entry => entry.ShareId == shareId))
            {
                return Task.FromResult<ICloudSyncOperationLease?>(null);
            }

            return Task.FromResult<ICloudSyncOperationLease?>(AddLease(
                new OperationEntry(
                    Guid.NewGuid(), shareId, OperationKind.PathMutation,
                    oldNormalized, newNormalized)));
        }
    }

    public Task<ICloudSyncOperationLease?> TryBeginShareMutationAsync(
        Guid shareId,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            PurgeExpiredReservations();
            if (_operations.Values.Any(entry => entry.ShareId == shareId) ||
                _externalReservations.Values.Any(entry => entry.ShareId == shareId))
            {
                return Task.FromResult<ICloudSyncOperationLease?>(null);
            }

            return Task.FromResult<ICloudSyncOperationLease?>(AddLease(
                new OperationEntry(
                    Guid.NewGuid(), shareId, OperationKind.ShareMutation,
                    "", null)));
        }
    }

    public Task<bool> TryReserveExternalPathMutationAsync(
        Guid shareId,
        string oldPath,
        string newPath,
        TimeSpan lifetime,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (lifetime <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(lifetime));

        string oldNormalized = ShareRelativePath.Normalize(oldPath);
        string newNormalized = ShareRelativePath.Normalize(newPath);
        lock (_gate)
        {
            PurgeExpiredReservations();
            if (_operations.Values.Any(entry => entry.ShareId == shareId))
                return Task.FromResult(false);

            var key = new ExternalMutationKey(
                shareId, oldNormalized.ToUpperInvariant(),
                newNormalized.ToUpperInvariant());
            _externalReservations[key] = new ExternalReservation(
                shareId, oldNormalized, newNormalized,
                timeProvider.GetUtcNow().Add(lifetime));
            return Task.FromResult(true);
        }
    }

    public Task CompleteExternalPathMutationAsync(
        Guid shareId,
        string oldPath,
        string newPath,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var key = new ExternalMutationKey(
            shareId,
            ShareRelativePath.Normalize(oldPath).ToUpperInvariant(),
            ShareRelativePath.Normalize(newPath).ToUpperInvariant());
        lock (_gate)
            _externalReservations.Remove(key);
        return Task.CompletedTask;
    }

    private ICloudSyncOperationLease AddLease(OperationEntry entry)
    {
        _operations.Add(entry.Id, entry);
        return new Lease(this, entry.Id);
    }

    private void Release(Guid id)
    {
        lock (_gate)
            _operations.Remove(id);
    }

    private void PurgeExpiredReservations()
    {
        DateTimeOffset now = timeProvider.GetUtcNow();
        foreach (var key in _externalReservations
                     .Where(pair => pair.Value.ExpiresAt <= now)
                     .Select(pair => pair.Key)
                     .ToArray())
            _externalReservations.Remove(key);
    }

    private sealed class Lease(
        InMemoryCloudSyncOperationCoordinator owner,
        Guid id) : ICloudSyncOperationLease
    {
        private int _disposed;

        public ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
                owner.Release(id);
            return ValueTask.CompletedTask;
        }
    }

    private enum OperationKind
    {
        Sync,
        PathMutation,
        ShareMutation
    }

    private sealed record OperationEntry(
        Guid Id,
        Guid ShareId,
        OperationKind Kind,
        string OldPath,
        string? NewPath);

    private sealed record ExternalMutationKey(
        Guid ShareId,
        string OldPath,
        string NewPath);

    private sealed record ExternalReservation(
        Guid ShareId,
        string OldPath,
        string NewPath,
        DateTimeOffset ExpiresAt);
}

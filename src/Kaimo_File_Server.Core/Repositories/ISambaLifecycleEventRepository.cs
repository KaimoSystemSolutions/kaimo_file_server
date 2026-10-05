namespace Kaimo_File_Server.Core.Repositories;

public enum SambaEventClaimResult
{
    Acquired,
    AlreadyCompleted,
    Busy,
    Conflict
}

public interface ISambaLifecycleEventRepository
{
    Task<SambaEventClaimResult> TryClaimAsync(
        Guid eventId,
        string eventType,
        TimeSpan leaseDuration,
        CancellationToken cancellationToken = default);

    Task CompleteAsync(
        Guid eventId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Extends the lease of a claimed, not yet completed event so a handler that runs
    /// longer than the lease (e.g. versioning a large close capture) is not re-claimed and
    /// executed a second time concurrently. Returns false if the event is no longer open, its
    /// lease has already expired, or it was re-claimed (an expired lease is never revived).
    /// </summary>
    Task<bool> RenewAsync(
        Guid eventId,
        TimeSpan leaseDuration,
        CancellationToken cancellationToken = default);

    Task ReleaseAsync(
        Guid eventId,
        string error,
        CancellationToken cancellationToken = default);

    Task<int> DeleteCompletedBeforeAsync(
        DateTime cutoffUtc,
        CancellationToken cancellationToken = default);
}

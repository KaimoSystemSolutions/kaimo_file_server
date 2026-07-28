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

    Task ReleaseAsync(
        Guid eventId,
        string error,
        CancellationToken cancellationToken = default);

    Task<int> DeleteCompletedBeforeAsync(
        DateTime cutoffUtc,
        CancellationToken cancellationToken = default);
}

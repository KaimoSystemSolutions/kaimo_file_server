namespace Kaimo_File_Server.Core.Repositories;

public enum SambaEventClaimResult
{
    Acquired,
    AlreadyCompleted,
    Busy,
    Conflict
}

/// <summary>
/// Outcome of a claim. When acquired, <see cref="Attempt"/> is the receipt's attempt count
/// written by this claim. Every claim increments it and renewals never change it, so it
/// identifies the claim for <see cref="ISambaLifecycleEventRepository.RenewAsync"/> and
/// <see cref="ISambaLifecycleEventRepository.ReleaseAsync"/>.
/// </summary>
public readonly record struct SambaEventClaim(SambaEventClaimResult Result, int Attempt = 0);

public interface ISambaLifecycleEventRepository
{
    Task<SambaEventClaim> TryClaimAsync(
        Guid eventId,
        string eventType,
        TimeSpan leaseDuration,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Marks the event done. Not tied to a claim: only a handler whose effects are applied
    /// calls it, so "done" is true even if a Samba retry has re-claimed the event meanwhile,
    /// and it keeps any further retry from running the effects again.
    /// </summary>
    Task CompleteAsync(
        Guid eventId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Extends the lease of a claimed, not yet completed event so a handler that runs
    /// longer than the lease (e.g. versioning a large close capture) is not re-claimed and
    /// executed a second time concurrently. Only the claim <paramref name="claimAttempt"/> is
    /// extended. Returns false if the event is no longer open, its lease has already expired,
    /// or it was re-claimed (an expired lease is never revived).
    /// </summary>
    Task<bool> RenewAsync(
        Guid eventId,
        int claimAttempt,
        TimeSpan leaseDuration,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Expires the lease so a Samba retry can re-claim the event, but only while the claim
    /// <paramref name="claimAttempt"/> is still the current one; a re-claim is left alone.
    /// </summary>
    Task ReleaseAsync(
        Guid eventId,
        int claimAttempt,
        string error,
        CancellationToken cancellationToken = default);

    Task<int> DeleteCompletedBeforeAsync(
        DateTime cutoffUtc,
        CancellationToken cancellationToken = default);
}

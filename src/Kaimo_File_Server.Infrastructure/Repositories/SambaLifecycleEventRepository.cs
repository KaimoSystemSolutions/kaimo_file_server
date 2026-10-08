using Kaimo_File_Server.Core.Domain;
using Kaimo_File_Server.Core.Repositories;
using Kaimo_File_Server.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Kaimo_File_Server.Infrastructure.Repositories;

public sealed class SambaLifecycleEventRepository(
    IDbContextFactory<ApplicationDbContext> dbFactory)
    : ISambaLifecycleEventRepository
{
    public async Task<SambaEventClaim> TryClaimAsync(
        Guid eventId,
        string eventType,
        TimeSpan leaseDuration,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(eventType);
        var now = DateTime.UtcNow;
        // PostgreSQL stores microseconds; truncating here lets the stored lease be compared
        // with this value in memory to recognize this call's own claim after a replay.
        var leaseUntil = TruncateToMicroseconds(now.Add(leaseDuration));

        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var reclaimed = await db.SambaLifecycleEventReceipts
            .Where(x => x.EventId == eventId &&
                        x.EventType == eventType &&
                        x.CompletedAtUtc == null &&
                        (x.LeaseUntilUtc == null || x.LeaseUntilUtc <= now))
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(x => x.LeaseUntilUtc, leaseUntil)
                .SetProperty(x => x.AttemptCount, x => x.AttemptCount + 1)
                .SetProperty(x => x.LastError, (string?)null),
                cancellationToken);

        // The UPDATE may already have claimed the event (directly or through a replay), so this
        // read must not be cancelled: an abandoned claim would stall the event until it expires.
        // The lease just written tells this claim apart from any later one; its attempt count
        // identifies the claim from now on. Should the lease already be gone, the event is left
        // to the next retry instead of risking an unowned run.
        var existing = await db.SambaLifecycleEventReceipts
            .AsNoTracking()
            .SingleOrDefaultAsync(x => x.EventId == eventId, CancellationToken.None);
        if (existing is not null)
        {
            if (!StringComparer.Ordinal.Equals(existing.EventType, eventType))
                return new(SambaEventClaimResult.Conflict);
            if (IsOwnClaim(db, existing, leaseUntil, replayed: reclaimed == 0))
                return new(SambaEventClaimResult.Acquired, existing.AttemptCount);
            return new(existing.CompletedAtUtc is not null
                ? SambaEventClaimResult.AlreadyCompleted
                : SambaEventClaimResult.Busy);
        }

        db.SambaLifecycleEventReceipts.Add(new SambaLifecycleEventReceipt
        {
            EventId = eventId,
            EventType = eventType,
            CreatedAtUtc = now,
            LeaseUntilUtc = leaseUntil,
            AttemptCount = 1
        });
        try
        {
            await db.SaveChangesAsync(cancellationToken);
            return new(SambaEventClaimResult.Acquired, 1);
        }
        catch (DbUpdateException)
        {
            // A concurrent bridge replica inserted the same event ID. Re-read
            // through a fresh context; the winner owns the active lease.
            await using var retryDb =
                await dbFactory.CreateDbContextAsync(cancellationToken);
            var winner = await retryDb.SambaLifecycleEventReceipts
                .AsNoTracking()
                .SingleAsync(x => x.EventId == eventId, cancellationToken);
            if (!StringComparer.Ordinal.Equals(winner.EventType, eventType))
                return new(SambaEventClaimResult.Conflict);
            if (IsOwnClaim(retryDb, winner, leaseUntil, replayed: true))
                return new(SambaEventClaimResult.Acquired, winner.AttemptCount);
            return new(winner.CompletedAtUtc is not null
                ? SambaEventClaimResult.AlreadyCompleted
                : SambaEventClaimResult.Busy);
        }
    }

    public async Task CompleteAsync(
        Guid eventId,
        CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var updated = await db.SambaLifecycleEventReceipts
            .Where(x => x.EventId == eventId && x.CompletedAtUtc == null)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(x => x.CompletedAtUtc, DateTime.UtcNow)
                .SetProperty(x => x.LeaseUntilUtc, (DateTime?)null)
                .SetProperty(x => x.LastError, (string?)null),
                cancellationToken);
        if (updated == 1)
            return;

        // A replayed statement (lost acknowledgement) finds its own completion. The event is
        // complete either way; failing here would release it and log a false error.
        if (await db.SambaLifecycleEventReceipts.AnyAsync(
                x => x.EventId == eventId && x.CompletedAtUtc != null, cancellationToken))
        {
            db.GetDatabaseLogger().LogWarning(
                "Samba lifecycle event {EventId} was already completed when completing it: the statement was " +
                "replayed after a lost acknowledgement, or an overlapping handler completed it first.", eventId);
            return;
        }

        throw new InvalidOperationException(
            $"Samba lifecycle event {eventId:N} has no active receipt.");
    }

    public async Task<bool> RenewAsync(
        Guid eventId,
        int claimAttempt,
        TimeSpan leaseDuration,
        CancellationToken cancellationToken = default)
    {
        var now = DateTime.UtcNow;
        var leaseUntil = now.Add(leaseDuration);
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        // Compare-and-swap on the claim's attempt count, which every re-claim increments and
        // renewals never change: a re-claim is never extended, neither when it happened before
        // this call nor while the UPDATE waited for the row lock of a running rename transaction.
        // Never revive an expired lease: a Samba retry may re-claim the event at any moment, and
        // the heartbeat must report the overlap risk instead of hiding it. A lease that only ran
        // out during the lock wait, without a re-claim, is still this handler's. A replayed
        // statement (lost acknowledgement) matches its own renewal again, so it stays idempotent.
        return await db.SambaLifecycleEventReceipts
            .Where(x => x.EventId == eventId &&
                        x.CompletedAtUtc == null &&
                        x.AttemptCount == claimAttempt &&
                        x.LeaseUntilUtc > now)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(x => x.LeaseUntilUtc, leaseUntil),
                cancellationToken) == 1;
    }

    public async Task ReleaseAsync(
        Guid eventId,
        int claimAttempt,
        string error,
        CancellationToken cancellationToken = default)
    {
        var sanitized = string.IsNullOrWhiteSpace(error)
            ? "Lifecycle handler failed."
            : error[..Math.Min(error.Length, 1000)];
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        // Only this claim is released: expiring a re-claim's lease would let a further retry
        // start while that handler is still running.
        await db.SambaLifecycleEventReceipts
            .Where(x => x.EventId == eventId && x.CompletedAtUtc == null && x.AttemptCount == claimAttempt)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(x => x.LeaseUntilUtc, DateTime.UtcNow)
                .SetProperty(x => x.LastError, sanitized),
                cancellationToken);
    }

    /// <summary>
    /// A receipt carrying exactly this call's lease is this call's claim, not a competitor's.
    /// That includes a claim statement retried after its acknowledgement was lost
    /// (<paramref name="replayed"/>); reporting it as busy would stall the event until the
    /// lease expires.
    /// </summary>
    private static bool IsOwnClaim(
        ApplicationDbContext db, SambaLifecycleEventReceipt receipt, DateTime leaseUntil, bool replayed)
    {
        if (receipt.CompletedAtUtc is not null || receipt.LeaseUntilUtc != leaseUntil)
            return false;

        if (replayed)
            db.GetDatabaseLogger().LogWarning(
                "Claim of Samba lifecycle event {EventId} was already applied by a statement whose " +
                "acknowledgement was lost; it is kept as acquired.", receipt.EventId);
        return true;
    }

    private static DateTime TruncateToMicroseconds(DateTime value)
        => new(value.Ticks - value.Ticks % 10, value.Kind);

    public async Task<int> DeleteCompletedBeforeAsync(
        DateTime cutoffUtc,
        CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        return await db.SambaLifecycleEventReceipts
            .Where(x => x.CompletedAtUtc != null &&
                        x.CompletedAtUtc < cutoffUtc)
            .ExecuteDeleteAsync(cancellationToken);
    }
}

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
    public async Task<SambaEventClaimResult> TryClaimAsync(
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
        if (reclaimed == 1)
            return SambaEventClaimResult.Acquired;

        var existing = await db.SambaLifecycleEventReceipts
            .AsNoTracking()
            .SingleOrDefaultAsync(x => x.EventId == eventId, cancellationToken);
        if (existing is not null)
        {
            if (!StringComparer.Ordinal.Equals(existing.EventType, eventType))
                return SambaEventClaimResult.Conflict;
            if (IsOwnReplayedClaim(db, existing, leaseUntil))
                return SambaEventClaimResult.Acquired;
            return existing.CompletedAtUtc is not null
                ? SambaEventClaimResult.AlreadyCompleted
                : SambaEventClaimResult.Busy;
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
            return SambaEventClaimResult.Acquired;
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
                return SambaEventClaimResult.Conflict;
            if (IsOwnReplayedClaim(retryDb, winner, leaseUntil))
                return SambaEventClaimResult.Acquired;
            return winner.CompletedAtUtc is not null
                ? SambaEventClaimResult.AlreadyCompleted
                : SambaEventClaimResult.Busy;
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
        TimeSpan leaseDuration,
        CancellationToken cancellationToken = default)
    {
        var now = DateTime.UtcNow;
        var leaseUntil = now.Add(leaseDuration);
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        // Never revive an expired lease: a Samba retry may already have re-claimed the
        // event, and extending the lease would only hide that two handlers now overlap.
        var current = await db.SambaLifecycleEventReceipts.AsNoTracking()
            .Where(x => x.EventId == eventId && x.CompletedAtUtc == null && x.LeaseUntilUtc > now)
            .Select(x => x.LeaseUntilUtc)
            .FirstOrDefaultAsync(cancellationToken);
        if (current is null)
            return false;

        // Compare-and-swap on the lease value just read, not on the clock: the UPDATE may wait
        // for the row lock of a running rename transaction, and a Samba retry that re-claims
        // the event meanwhile changes the lease, so this renewal then matches nothing. A lease
        // that only ran out during the wait, without a re-claim, is still this handler's.
        if (await db.SambaLifecycleEventReceipts
                .Where(x => x.EventId == eventId && x.CompletedAtUtc == null && x.LeaseUntilUtc == current)
                .ExecuteUpdateAsync(setters => setters
                    .SetProperty(x => x.LeaseUntilUtc, leaseUntil),
                    cancellationToken) == 1)
            return true;

        // A replayed statement (lost acknowledgement) finds exactly the lease it wrote.
        return await db.SambaLifecycleEventReceipts.AnyAsync(
            x => x.EventId == eventId && x.CompletedAtUtc == null && x.LeaseUntilUtc == leaseUntil,
            cancellationToken);
    }

    public async Task ReleaseAsync(
        Guid eventId,
        string error,
        CancellationToken cancellationToken = default)
    {
        var sanitized = string.IsNullOrWhiteSpace(error)
            ? "Lifecycle handler failed."
            : error[..Math.Min(error.Length, 1000)];
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        await db.SambaLifecycleEventReceipts
            .Where(x => x.EventId == eventId && x.CompletedAtUtc == null)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(x => x.LeaseUntilUtc, DateTime.UtcNow)
                .SetProperty(x => x.LastError, sanitized),
                cancellationToken);
    }

    /// <summary>
    /// A claim statement that was retried after its acknowledgement was lost finds the receipt
    /// already carrying exactly this call's lease. That is this call's claim, not a competitor's;
    /// reporting it as busy would stall the event until the lease expires.
    /// </summary>
    private static bool IsOwnReplayedClaim(
        ApplicationDbContext db, SambaLifecycleEventReceipt receipt, DateTime leaseUntil)
    {
        if (receipt.CompletedAtUtc is not null || receipt.LeaseUntilUtc != leaseUntil)
            return false;

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

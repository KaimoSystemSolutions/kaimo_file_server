using Kaimo_File_Server.Core.Domain;
using Kaimo_File_Server.Core.Repositories;
using Kaimo_File_Server.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

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
        var leaseUntil = now.Add(leaseDuration);

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
        if (updated != 1)
            throw new InvalidOperationException(
                $"Samba lifecycle event {eventId:N} has no active receipt.");
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

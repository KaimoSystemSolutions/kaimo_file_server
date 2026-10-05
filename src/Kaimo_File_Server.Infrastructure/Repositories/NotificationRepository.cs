using Kaimo_File_Server.Core.Domain.Notifications;
using Kaimo_File_Server.Core.Repositories;
using Kaimo_File_Server.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Kaimo_File_Server.Infrastructure.Repositories;

public sealed class NotificationRepository(IDbContextFactory<ApplicationDbContext> dbFactory)
    : INotificationRepository
{
    // -- Outbox --

    public Task AddEventAsync(NotificationEvent notification, CancellationToken ct = default)
    {
        bool attempted = false;
        return dbFactory.ExecuteResilientAsync(async db =>
        {
            bool isReplay = attempted;
            attempted = true;

            // A replay may follow a COMMIT whose acknowledgement was lost. Inserting again would
            // store a second event under a new Seq and mail every recipient twice. The event has
            // no client-side key, so the replay looks for the identical row instead (same type,
            // timestamp to the microsecond, actor, subjects and payload).
            if (isReplay)
            {
                var existingSeq = await db.NotificationEvents.AsNoTracking()
                    .Where(e => e.Type == notification.Type
                                && e.OccurredAtUtc == notification.OccurredAtUtc
                                && e.ActorUserId == notification.ActorUserId
                                && e.SubjectUserIdsJson == notification.SubjectUserIdsJson
                                && e.PayloadJson == notification.PayloadJson)
                    .Select(e => (long?)e.Seq)
                    .FirstOrDefaultAsync(ct);
                if (existingSeq is { } seq)
                {
                    db.GetDatabaseLogger().LogWarning(
                        "Notification event {Type} was already stored as {Seq} by an attempt whose commit " +
                        "acknowledgement was lost; the replay does not insert it again.",
                        notification.Type, seq);
                    notification.Seq = seq;
                    return;
                }
            }

            // Never reuse an identity value a failed attempt may have written back.
            notification.Seq = 0;
            db.NotificationEvents.Add(notification);
            await db.SaveChangesAsync(ct);
        }, ct);
    }

    public async Task<List<NotificationEvent>> ClaimEventsAsync(
        int max, DateTime leaseUntilUtc, CancellationToken ct = default)
    {
        var now = DateTime.UtcNow;
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var candidates = await db.NotificationEvents.AsNoTracking()
            .Where(e => e.Status == NotificationEventStatus.Pending
                        || (e.Status == NotificationEventStatus.Processing && e.LeaseUntilUtc <= now))
            .OrderBy(e => e.Seq)
            .Select(e => e.Seq)
            .Take(max)
            .ToListAsync(ct);

        var claimed = new List<long>();
        foreach (var seq in candidates)
        {
            // Conditional update: only one claimer wins an event, even if a second
            // dispatcher ever ran concurrently.
            var updated = await db.NotificationEvents
                .Where(e => e.Seq == seq
                            && (e.Status == NotificationEventStatus.Pending
                                || (e.Status == NotificationEventStatus.Processing && e.LeaseUntilUtc <= now)))
                .ExecuteUpdateAsync(s => s
                    .SetProperty(e => e.Status, NotificationEventStatus.Processing)
                    .SetProperty(e => e.LeaseUntilUtc, leaseUntilUtc)
                    .SetProperty(e => e.AttemptCount, e => e.AttemptCount + 1), ct);
            // A replayed statement (lost acknowledgement) finds the event already claimed with
            // exactly this lease and matches no row. The lease timestamp identifies this call,
            // so that is our claim, not a competitor's.
            if (updated == 0 && await db.NotificationEvents.AnyAsync(
                    e => e.Seq == seq
                         && e.Status == NotificationEventStatus.Processing
                         && e.LeaseUntilUtc == leaseUntilUtc, ct))
            {
                db.GetDatabaseLogger().LogWarning(
                    "Claim of notification event {Seq} was already applied by a statement whose " +
                    "acknowledgement was lost; it is kept as claimed.", seq);
                updated = 1;
            }
            if (updated == 1) claimed.Add(seq);
        }

        return claimed.Count == 0
            ? []
            : await db.NotificationEvents.AsNoTracking()
                .Where(e => claimed.Contains(e.Seq)).OrderBy(e => e.Seq).ToListAsync(ct);
    }

    public async Task CompleteEventAsync(
        long seq, NotificationEventStatus status, string? error, CancellationToken ct = default,
        DateTime? claimedLeaseUntilUtc = null)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        if (await UpdateEventStatusAsync(db, seq, status, error, claimedLeaseUntilUtc, ct) == 0)
            db.GetDatabaseLogger().LogWarning(
                "Completing notification event {Seq} as {Status} matched no claimed event: it was already " +
                "completed, or the statement was replayed after a lost acknowledgement.", seq, status);
    }

    public Task CompleteEventWithDeliveriesAsync(
        long seq, IReadOnlyCollection<MailDelivery> deliveries, NotificationEventStatus status, CancellationToken ct = default,
        DateTime? claimedLeaseUntilUtc = null)
        => dbFactory.ExecuteResilientAsync(async db =>
        {
            await using var tx = await db.Database.BeginTransactionAsync(ct);
            // Complete first: the conditional update row-locks the event, so a concurrent or
            // replayed completion waits for this commit and then matches no row. Deliveries are
            // therefore stored exactly once; a replay whose first COMMIT went through
            // (acknowledgement lost) neither inserts them again nor sends the event back to Pending.
            if (await UpdateEventStatusAsync(db, seq, status, null, claimedLeaseUntilUtc, ct) == 0)
            {
                db.GetDatabaseLogger().LogWarning(
                    "Notification event {Seq} was no longer claimed by this dispatcher when its {Count} deliveries " +
                    "were stored: an earlier attempt already committed them (lost acknowledgement), or the lease " +
                    "expired and the event was re-claimed or completed elsewhere. Nothing was stored again.",
                    seq, deliveries.Count);
                return;
            }

            if (deliveries.Count > 0)
            {
                db.MailDeliveries.AddRange(deliveries);
                await db.SaveChangesAsync(ct);
            }
            await tx.CommitAsync(ct);
        }, ct);

    // Only a claimed (Processing) event can be completed: a late or replayed completion must
    // never turn an event that already reached Done/Skipped/Failed back into Pending. With the
    // claim's lease value, only the claim that is still current may complete it: once a lease
    // expired and the event was re-claimed, the former holder's completion matches nothing.
    private static Task<int> UpdateEventStatusAsync(
        ApplicationDbContext db, long seq, NotificationEventStatus status, string? error,
        DateTime? claimedLeaseUntilUtc, CancellationToken ct)
        => db.NotificationEvents
            .Where(e => e.Seq == seq && e.Status == NotificationEventStatus.Processing)
            .Where(e => claimedLeaseUntilUtc == null || e.LeaseUntilUtc == claimedLeaseUntilUtc)
            .ExecuteUpdateAsync(s => s
                .SetProperty(e => e.Status, status)
                .SetProperty(e => e.LeaseUntilUtc, (DateTime?)null)
                .SetProperty(e => e.LastError, Truncate(error))
                .SetProperty(e => e.ProcessedAtUtc, status == NotificationEventStatus.Pending ? null : DateTime.UtcNow), ct);

    // -- Deliveries --

    public async Task<List<MailDelivery>> ClaimDueDeliveriesAsync(
        int max, DateTime nowUtc, TimeSpan staleAfter, CancellationToken ct = default)
    {
        var staleBefore = nowUtc - staleAfter;
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var candidates = await db.MailDeliveries.AsNoTracking()
            .Where(d => ((d.Status == MailDeliveryStatus.Pending || d.Status == MailDeliveryStatus.Failed)
                         && d.NextAttemptUtc <= nowUtc)
                        || (d.Status == MailDeliveryStatus.Sending && d.NextAttemptUtc <= staleBefore))
            .OrderBy(d => d.NextAttemptUtc)
            .Select(d => d.Id)
            .Take(max)
            .ToListAsync(ct);

        var claimed = new List<Guid>();
        foreach (var id in candidates)
        {
            var updated = await db.MailDeliveries
                .Where(d => d.Id == id
                            && (((d.Status == MailDeliveryStatus.Pending || d.Status == MailDeliveryStatus.Failed)
                                 && d.NextAttemptUtc <= nowUtc)
                                || (d.Status == MailDeliveryStatus.Sending && d.NextAttemptUtc <= staleBefore)))
                .ExecuteUpdateAsync(s => s
                    .SetProperty(d => d.Status, MailDeliveryStatus.Sending)
                    // Marks when sending started; drives the stale-Sending recovery above.
                    .SetProperty(d => d.NextAttemptUtc, nowUtc), ct);
            // A replayed statement (lost acknowledgement) finds the delivery already in Sending
            // with exactly this call's start time and matches no row. That is our claim; treating
            // it as lost would park the mail until the stale-Sending recovery picks it up.
            if (updated == 0 && await db.MailDeliveries.AnyAsync(
                    d => d.Id == id && d.Status == MailDeliveryStatus.Sending && d.NextAttemptUtc == nowUtc, ct))
            {
                db.GetDatabaseLogger().LogWarning(
                    "Claim of mail delivery {DeliveryId} was already applied by a statement whose " +
                    "acknowledgement was lost; it is kept as claimed.", id);
                updated = 1;
            }
            if (updated == 1) claimed.Add(id);
        }

        return claimed.Count == 0
            ? []
            : await db.MailDeliveries.AsNoTracking().Where(d => claimed.Contains(d.Id)).ToListAsync(ct);
    }

    public async Task UpdateDeliveryAsync(MailDelivery delivery, CancellationToken ct = default)
    {
        delivery.LastError = Truncate(delivery.LastError);
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        db.MailDeliveries.Update(delivery);
        await db.SaveChangesAsync(ct);
    }

    public async Task<bool> WasDeliveredRecentlyAsync(
        Guid ruleId, string dedupKey, string toAddress, DateTime sinceUtc, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        return await db.MailDeliveries.AnyAsync(d =>
            d.RuleId == ruleId && d.DedupKey == dedupKey && d.ToAddress == toAddress
            && d.CreatedAtUtc >= sinceUtc, ct);
    }

    public async Task<List<MailDelivery>> GetRecentDeliveriesAsync(int max, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        return await db.MailDeliveries.AsNoTracking()
            .OrderByDescending(d => d.CreatedAtUtc).Take(max).ToListAsync(ct);
    }

    public async Task<MailDelivery?> GetDeliveryAsync(Guid id, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        return await db.MailDeliveries.AsNoTracking().FirstOrDefaultAsync(d => d.Id == id, ct);
    }

    public async Task<int> CountPendingDeliveriesAsync(CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        return await db.MailDeliveries.CountAsync(d =>
            d.Status == MailDeliveryStatus.Pending || d.Status == MailDeliveryStatus.Failed, ct);
    }

    public async Task<Dictionary<Guid, DateTime>> GetLastSentPerRuleAsync(CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var rows = await db.MailDeliveries.AsNoTracking()
            .Where(d => d.Status == MailDeliveryStatus.Sent && d.SentAtUtc != null)
            .GroupBy(d => d.RuleId)
            .Select(g => new { RuleId = g.Key, Last = g.Max(d => d.SentAtUtc) })
            .ToListAsync(ct);
        return rows.ToDictionary(r => r.RuleId, r => r.Last!.Value);
    }

    public async Task<int> PruneAsync(DateTime cutoffUtc, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var events = await db.NotificationEvents
            .Where(e => (e.Status == NotificationEventStatus.Done
                         || e.Status == NotificationEventStatus.Skipped
                         || e.Status == NotificationEventStatus.Failed)
                        && e.ProcessedAtUtc < cutoffUtc)
            .ExecuteDeleteAsync(ct);
        var deliveries = await db.MailDeliveries
            .Where(d => (d.Status == MailDeliveryStatus.Sent || d.Status == MailDeliveryStatus.Dead)
                        && d.CreatedAtUtc < cutoffUtc)
            .ExecuteDeleteAsync(ct);
        return events + deliveries;
    }

    // -- Rules --

    public async Task<List<MailRule>> GetRulesAsync(CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        return await db.MailRules.AsNoTracking().OrderBy(r => r.EventType).ThenBy(r => r.Name).ToListAsync(ct);
    }

    public async Task<List<MailRule>> GetEnabledRulesAsync(string eventType, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        return await db.MailRules.AsNoTracking()
            .Where(r => r.Enabled && r.EventType == eventType).ToListAsync(ct);
    }

    public async Task<HashSet<string>> GetEnabledRuleTypesAsync(CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        return (await db.MailRules.Where(r => r.Enabled).Select(r => r.EventType).Distinct().ToListAsync(ct))
            .ToHashSet(StringComparer.Ordinal);
    }

    public async Task SaveRuleAsync(MailRule rule, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var exists = await db.MailRules.AnyAsync(r => r.Id == rule.Id, ct);
        if (exists) db.MailRules.Update(rule);
        else db.MailRules.Add(rule);
        await db.SaveChangesAsync(ct);
    }

    public async Task DeleteRuleAsync(Guid id, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        await db.MailRules.Where(r => r.Id == id).ExecuteDeleteAsync(ct);
    }

    // -- Templates --

    public async Task<List<MailTemplate>> GetTemplatesAsync(CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        return await db.MailTemplates.AsNoTracking().ToListAsync(ct);
    }

    public async Task<MailTemplate?> GetTemplateAsync(string eventType, string language, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        return await db.MailTemplates.AsNoTracking()
            .FirstOrDefaultAsync(t => t.EventType == eventType && t.Language == language, ct);
    }

    public async Task SaveTemplateAsync(MailTemplate template, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var existing = await db.MailTemplates.FirstOrDefaultAsync(
            t => t.EventType == template.EventType && t.Language == template.Language, ct);
        if (existing is null)
        {
            db.MailTemplates.Add(template);
        }
        else
        {
            existing.SubjectTemplate = template.SubjectTemplate;
            existing.HtmlTemplate = template.HtmlTemplate;
            existing.TextTemplate = template.TextTemplate;
            existing.UpdatedAtUtc = template.UpdatedAtUtc;
            existing.UpdatedByUserId = template.UpdatedByUserId;
        }
        await db.SaveChangesAsync(ct);
    }

    public async Task DeleteTemplateAsync(string eventType, string language, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        await db.MailTemplates.Where(t => t.EventType == eventType && t.Language == language).ExecuteDeleteAsync(ct);
    }

    private static string? Truncate(string? value)
        => value is { Length: > 1000 } ? value[..1000] : value;
}

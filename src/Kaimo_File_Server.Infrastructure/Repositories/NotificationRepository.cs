using Kaimo_File_Server.Core.Domain.Notifications;
using Kaimo_File_Server.Core.Repositories;
using Kaimo_File_Server.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Kaimo_File_Server.Infrastructure.Repositories;

public sealed class NotificationRepository(IDbContextFactory<ApplicationDbContext> dbFactory)
    : INotificationRepository
{
    // -- Outbox --

    public async Task AddEventAsync(NotificationEvent notification, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        db.NotificationEvents.Add(notification);
        await db.SaveChangesAsync(ct);
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
            if (updated == 1) claimed.Add(seq);
        }

        return claimed.Count == 0
            ? []
            : await db.NotificationEvents.AsNoTracking()
                .Where(e => claimed.Contains(e.Seq)).OrderBy(e => e.Seq).ToListAsync(ct);
    }

    public async Task CompleteEventAsync(
        long seq, NotificationEventStatus status, string? error, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        await db.NotificationEvents.Where(e => e.Seq == seq)
            .ExecuteUpdateAsync(s => s
                .SetProperty(e => e.Status, status)
                .SetProperty(e => e.LeaseUntilUtc, (DateTime?)null)
                .SetProperty(e => e.LastError, Truncate(error))
                .SetProperty(e => e.ProcessedAtUtc, status == NotificationEventStatus.Pending ? null : DateTime.UtcNow), ct);
    }

    // -- Deliveries --

    public async Task AddDeliveriesAsync(IEnumerable<MailDelivery> deliveries, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        db.MailDeliveries.AddRange(deliveries);
        await db.SaveChangesAsync(ct);
    }

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

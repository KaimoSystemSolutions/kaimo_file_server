using Kaimo_File_Server.Core.Domain.Notifications;

namespace Kaimo_File_Server.Core.Repositories;

/// <summary>
/// Persistence for mail notifications: the event outbox, the rendered deliveries, the rules
/// and the customized templates. One repository because the four tables are only ever used
/// together by the publisher, the dispatcher and the notifications page.
/// </summary>
public interface INotificationRepository
{
    // -- Outbox --
    Task AddEventAsync(NotificationEvent notification, CancellationToken ct = default);

    /// <summary>
    /// Atomically claims up to <paramref name="max"/> events that are pending or whose lease
    /// expired, in <see cref="NotificationEvent.Seq"/> order. Claimed events are
    /// <see cref="NotificationEventStatus.Processing"/> until <paramref name="leaseUntilUtc"/>.
    /// </summary>
    Task<List<NotificationEvent>> ClaimEventsAsync(int max, DateTime leaseUntilUtc, CancellationToken ct = default);

    /// <summary>
    /// Completes a claimed event. With <paramref name="claimedLeaseUntilUtc"/> (the lease the
    /// claim stored), only that claim may complete it, never one whose lease already expired.
    /// </summary>
    Task CompleteEventAsync(long seq, NotificationEventStatus status, string? error, CancellationToken ct = default,
        DateTime? claimedLeaseUntilUtc = null);

    /// <summary>
    /// Stores the rendered <paramref name="deliveries"/> and completes the event in one
    /// transaction, so a crash in between can never leave deliveries behind for an event that
    /// is later re-claimed and rendered a second time (duplicate mails).
    /// </summary>
    Task CompleteEventWithDeliveriesAsync(
        long seq, IReadOnlyCollection<MailDelivery> deliveries, NotificationEventStatus status, CancellationToken ct = default,
        DateTime? claimedLeaseUntilUtc = null);

    // -- Deliveries --

    /// <summary>
    /// Atomically claims up to <paramref name="max"/> pending/failed deliveries that are due,
    /// setting them to <see cref="MailDeliveryStatus.Sending"/>. A delivery stuck in Sending
    /// (e.g. the process died mid-send) becomes claimable again after <paramref name="staleAfter"/>.
    /// </summary>
    Task<List<MailDelivery>> ClaimDueDeliveriesAsync(int max, DateTime nowUtc, TimeSpan staleAfter, CancellationToken ct = default);

    Task UpdateDeliveryAsync(MailDelivery delivery, CancellationToken ct = default);

    /// <summary>Whether a mail for this rule, dedup key and address was created at or after <paramref name="sinceUtc"/>.</summary>
    Task<bool> WasDeliveredRecentlyAsync(Guid ruleId, string dedupKey, string toAddress, DateTime sinceUtc, CancellationToken ct = default);

    Task<List<MailDelivery>> GetRecentDeliveriesAsync(int max, CancellationToken ct = default);
    Task<MailDelivery?> GetDeliveryAsync(Guid id, CancellationToken ct = default);
    Task<int> CountPendingDeliveriesAsync(CancellationToken ct = default);

    /// <summary>Last successful send per rule id.</summary>
    Task<Dictionary<Guid, DateTime>> GetLastSentPerRuleAsync(CancellationToken ct = default);

    /// <summary>Deletes finished events and sent/dead deliveries older than the cutoff.</summary>
    Task<int> PruneAsync(DateTime cutoffUtc, CancellationToken ct = default);

    // -- Rules --
    Task<List<MailRule>> GetRulesAsync(CancellationToken ct = default);
    Task<List<MailRule>> GetEnabledRulesAsync(string eventType, CancellationToken ct = default);

    /// <summary>The event types that have at least one enabled rule (publisher shortcut).</summary>
    Task<HashSet<string>> GetEnabledRuleTypesAsync(CancellationToken ct = default);

    Task SaveRuleAsync(MailRule rule, CancellationToken ct = default);
    Task DeleteRuleAsync(Guid id, CancellationToken ct = default);

    // -- Templates --
    Task<List<MailTemplate>> GetTemplatesAsync(CancellationToken ct = default);
    Task<MailTemplate?> GetTemplateAsync(string eventType, string language, CancellationToken ct = default);

    /// <summary>Inserts or updates the customization of (event type, language).</summary>
    Task SaveTemplateAsync(MailTemplate template, CancellationToken ct = default);

    /// <summary>Removes the customization so the built-in default applies again.</summary>
    Task DeleteTemplateAsync(string eventType, string language, CancellationToken ct = default);
}

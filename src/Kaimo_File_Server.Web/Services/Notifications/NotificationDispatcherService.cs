using System.Text.Json;
using Kaimo_File_Server.Core.Domain.Notifications;
using Kaimo_File_Server.Core.Repositories;
using Kaimo_File_Server.Core.Security;
using Kaimo_File_Server.Core.Services.Notifications;
using Kaimo_File_Server.Infrastructure.Configuration;
using Kaimo_File_Server.Infrastructure.Notifications;

namespace Kaimo_File_Server.Web.Services.Notifications;

/// <summary>
/// Turns outbox events into mails and sends them. Single owner: registered only in the Web
/// process, like <c>SearchIndexingService</c>. Each cycle
/// <list type="number">
/// <item>claims pending events (with a lease, so a crashed cycle is picked up again),</item>
/// <item>matches the enabled rules, resolves and throttles recipients, renders one delivery per
/// recipient in the default language, and</item>
/// <item>sends due deliveries within <see cref="SmtpSettings.MaxMailsPerMinute"/>, retrying with
/// exponential backoff until a delivery is given up as <see cref="MailDeliveryStatus.Dead"/>.</item>
/// </list>
/// While SMTP is disabled or incomplete, deliveries simply stay pending.
/// </summary>
public sealed class NotificationDispatcherService(
    IServiceScopeFactory scopeFactory,
    INotificationRepository repository,
    NotificationDispatchSignal signal,
    ISmtpConfigStore smtpStore,
    ISmtpMailSender sender,
    NotificationMailComposer composer,
    TimeProvider time,
    ILogger<NotificationDispatcherService> logger,
    DemoModeOptions? demo = null) : BackgroundService
{
    public const int MaxDeliveryAttempts = 8;
    public const int MaxEventAttempts = 5;
    public const string SeededTypesKey = "notifications.seeded_event_types";
    public static readonly TimeSpan RetentionPeriod = TimeSpan.FromDays(30);

    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan EventLease = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan StaleSending = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan PruneInterval = TimeSpan.FromHours(6);

    // Send timestamps of the last minute (single dispatcher → in-memory is enough).
    private readonly Queue<DateTime> _recentSends = new();
    private DateTime _nextPruneUtc;

    // While the SMTP server is unusable, sending pauses with a growing backoff; mails keep
    // their own retry budget for failures that concern them individually.
    // The pause is lifted as soon as the settings change (records compare by value), so a fix
    // by the administrator takes effect on the next cycle.
    private static readonly TimeSpan MaxTransportPause = TimeSpan.FromMinutes(15);
    private int _transportFailures;
    private DateTime _transportPausedUntilUtc;
    private SmtpSettings? _transportPausedFor;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // The read-only demo sends nothing.
        if (demo?.ReadOnly == true) return;

        try { await SeedDefaultRulesAsync(stoppingToken); }
        catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
        {
            logger.LogWarning(ex, "Seeding the default notification rules failed");
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await RunOnceAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Notification dispatch cycle failed");
            }

            try { await signal.WaitAsync(PollInterval, stoppingToken); }
            catch (OperationCanceledException) { break; }
        }
    }

    /// <summary>One full cycle: process events, send due mails, prune now and then.</summary>
    public async Task RunOnceAsync(CancellationToken ct)
    {
        await ProcessEventsAsync(ct);
        await SendDueAsync(ct);

        var now = time.GetUtcNow().UtcDateTime;
        if (now >= _nextPruneUtc)
        {
            _nextPruneUtc = now + PruneInterval;
            var removed = await repository.PruneAsync(now - RetentionPeriod, ct);
            if (removed > 0)
                logger.LogInformation("Pruned {Count} old notification events/deliveries", removed);
        }
    }

    /// <summary>
    /// Adds a disabled default rule for every catalog event that never had one seeded — on the
    /// first start for all events, after an update for newly added ones. Deleted rules are not
    /// re-created, because the seeded event types are remembered in the configuration.
    /// </summary>
    public async Task SeedDefaultRulesAsync(CancellationToken ct)
    {
        using var scope = scopeFactory.CreateScope();
        var config = scope.ServiceProvider.GetRequiredService<IConfigRepository>();
        var seeded = (await config.GetFreshAsync(SeededTypesKey, new List<string>())).ToHashSet(StringComparer.Ordinal);
        var missing = NotificationEventCatalog.All.Where(d => !seeded.Contains(d.Type)).ToList();
        if (missing.Count == 0) return;

        foreach (var definition in missing)
        {
            var rule = new MailRule
            {
                EventType = definition.Type,
                Name = "Default",
                Enabled = false,
                ThrottleMinutes = definition.DefaultThrottleMinutes,
            };
            rule.SetRecipients(definition.DefaultRecipients);
            await repository.SaveRuleAsync(rule, ct);
            seeded.Add(definition.Type);
        }
        await config.SetAsync(SeededTypesKey, seeded.Order(StringComparer.Ordinal).ToList());
    }

    public async Task ProcessEventsAsync(CancellationToken ct)
    {
        var now = time.GetUtcNow().UtcDateTime;
        var events = await repository.ClaimEventsAsync(20, now + EventLease, ct);
        if (events.Count == 0) return;

        var environment = await composer.LoadEnvironmentAsync();
        using var scope = scopeFactory.CreateScope();
        var resolver = scope.ServiceProvider.GetRequiredService<NotificationRecipientResolver>();

        foreach (var notification in events)
        {
            try
            {
                // The claim already counted this attempt. An event above the limit never reached
                // the catch below on its earlier attempts: the process stopped while handling it,
                // or its lease ran out first. Park it as Failed instead of re-claiming it forever.
                // Inside the try, so a database error here does not abandon the rest of the batch.
                if (notification.AttemptCount > MaxEventAttempts)
                {
                    logger.LogWarning(
                        "Notification event {Seq} ({Type}) was claimed {Attempts} times without completing; parked as failed.",
                        notification.Seq, notification.Type, notification.AttemptCount);
                    await repository.CompleteEventAsync(notification.Seq, NotificationEventStatus.Failed,
                        "Exceeded the maximum number of attempts without completing " +
                        "(the process stopped or the processing lease expired).", ct,
                        claimedLeaseUntilUtc: notification.LeaseUntilUtc);
                    continue;
                }

                // The claim's stored lease identifies this claim: if it expired while the event
                // was being rendered and the event was re-claimed, this completion is discarded.
                var created = await BuildDeliveriesAsync(notification, environment, resolver, ct);
                await repository.CompleteEventWithDeliveriesAsync(notification.Seq, created,
                    created.Count > 0 ? NotificationEventStatus.Done : NotificationEventStatus.Skipped, ct,
                    claimedLeaseUntilUtc: notification.LeaseUntilUtc);
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                logger.LogWarning(ex, "Processing notification event {Seq} ({Type}) failed", notification.Seq, notification.Type);
                await repository.CompleteEventAsync(notification.Seq,
                    notification.AttemptCount >= MaxEventAttempts ? NotificationEventStatus.Failed : NotificationEventStatus.Pending,
                    ex.Message, ct, claimedLeaseUntilUtc: notification.LeaseUntilUtc);
            }
        }
    }

    private async Task<List<MailDelivery>> BuildDeliveriesAsync(
        NotificationEvent notification, MailEnvironment environment,
        NotificationRecipientResolver resolver, CancellationToken ct)
    {
        var result = new List<MailDelivery>();
        if (NotificationEventCatalog.Find(notification.Type) is null) return result;
        var rules = await repository.GetEnabledRulesAsync(notification.Type, ct);
        if (rules.Count == 0) return result;

        var payload = JsonSerializer.Deserialize<Dictionary<string, string>>(notification.PayloadJson) ?? [];
        var contextUsers = JsonSerializer.Deserialize<Dictionary<string, Guid>>(notification.SubjectUserIdsJson) ?? [];

        // ponytail: one language for all recipients (the app language); a per-user language
        // setting does not exist yet. Add it here once users can choose a language.
        var language = environment.DefaultLanguage;
        var template = await composer.GetTemplateAsync(notification.Type, language, ct);

        // Several rules share one template, so an address gets the mail of an event only once.
        var addressed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var now = time.GetUtcNow().UtcDateTime;

        foreach (var rule in rules)
        {
            foreach (var recipient in await resolver.ResolveAsync(rule.GetRecipients(), contextUsers))
            {
                if (!addressed.Add(recipient.Address)) continue;
                if (rule.ThrottleMinutes > 0 && notification.DedupKey is { } key
                    && await repository.WasDeliveredRecentlyAsync(
                        rule.Id, key, recipient.Address, now.AddMinutes(-rule.ThrottleMinutes), ct))
                    continue;

                var mail = await composer.RenderAsync(template, environment, payload, language,
                    recipient.Name, recipient.Address, notification.OccurredAtUtc);
                result.Add(new MailDelivery
                {
                    EventSeq = notification.Seq,
                    RuleId = rule.Id,
                    EventType = notification.Type,
                    DedupKey = notification.DedupKey,
                    ToAddress = recipient.Address,
                    ToUserId = recipient.UserId,
                    Language = language,
                    Subject = mail.Subject,
                    HtmlBody = mail.Html,
                    TextBody = mail.Text,
                    CreatedAtUtc = now,
                    NextAttemptUtc = now,
                });
            }
        }
        return result;
    }

    public async Task SendDueAsync(CancellationToken ct)
    {
        var settings = await smtpStore.GetAsync();
        if (!settings.IsUsable) return;

        var now = time.GetUtcNow().UtcDateTime;
        if (now < _transportPausedUntilUtc && settings == _transportPausedFor) return;
        while (_recentSends.Count > 0 && _recentSends.Peek() <= now.AddMinutes(-1)) _recentSends.Dequeue();
        var budget = settings.MaxMailsPerMinute - _recentSends.Count;
        if (budget <= 0) return;

        var due = await repository.ClaimDueDeliveriesAsync(budget, now, StaleSending, ct);
        if (due.Count == 0) return;

        var environment = await composer.LoadEnvironmentAsync();
        string? lastError = null;
        var anySuccess = false;
        for (var i = 0; i < due.Count; i++)
        {
            var delivery = due[i];
            _recentSends.Enqueue(time.GetUtcNow().UtcDateTime);
            try
            {
                await sender.SendAsync(NotificationMailComposer.ToMessage(
                    environment, delivery.ToAddress, delivery.Subject, delivery.HtmlBody, delivery.TextBody), ct);
                delivery.Status = MailDeliveryStatus.Sent;
                delivery.SentAtUtc = time.GetUtcNow().UtcDateTime;
                delivery.LastError = null;
                anySuccess = true;
                _transportFailures = 0;
            }
            catch (MailSendException ex) when (ex.Transport && !ct.IsCancellationRequested)
            {
                // The server is unusable, not this mail: pause sending, keep every mail's retry
                // budget, and hand the rest of the batch back instead of waiting for each timeout.
                lastError = ex.Message;
                _transportFailures++;
                var pause = Backoff(_transportFailures);
                _transportPausedUntilUtc = time.GetUtcNow().UtcDateTime + (pause < MaxTransportPause ? pause : MaxTransportPause);
                _transportPausedFor = settings;
                foreach (var rest in due.Skip(i))
                {
                    // Due again right away; the in-memory pause above decides when sending resumes.
                    rest.Status = MailDeliveryStatus.Failed;
                    rest.NextAttemptUtc = time.GetUtcNow().UtcDateTime;
                    rest.LastError = ex.Message;
                    await repository.UpdateDeliveryAsync(rest, ct);
                }
                break;
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                delivery.AttemptCount++;
                delivery.LastError = ex.Message;
                var permanent = ex is MailSendException { Permanent: true };
                // A rejected recipient says nothing about the connection itself.
                if (!permanent) lastError = ex.Message;
                if (permanent || delivery.AttemptCount >= MaxDeliveryAttempts)
                {
                    delivery.Status = MailDeliveryStatus.Dead;
                }
                else
                {
                    delivery.Status = MailDeliveryStatus.Failed;
                    delivery.NextAttemptUtc = time.GetUtcNow().UtcDateTime + Backoff(delivery.AttemptCount);
                }
            }
            await repository.UpdateDeliveryAsync(delivery, ct);
        }

        // Keeps the "connection status" line of the mail-server settings current.
        if (lastError is not null) await smtpStore.RecordStatusAsync(lastError);
        else if (anySuccess) await smtpStore.RecordStatusAsync(null);
    }

    /// <summary>1 min, 2 min, 4 min … capped at 6 h.</summary>
    public static TimeSpan Backoff(int attempt)
        => TimeSpan.FromMinutes(Math.Min(360, Math.Pow(2, Math.Max(0, attempt - 1))));
}

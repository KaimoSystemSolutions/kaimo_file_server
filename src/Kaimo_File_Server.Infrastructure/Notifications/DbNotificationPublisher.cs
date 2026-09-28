using Kaimo_File_Server.Core.Domain.Notifications;
using Kaimo_File_Server.Core.Repositories;
using Kaimo_File_Server.Core.Security;
using Kaimo_File_Server.Core.Services.Notifications;
using Microsoft.Extensions.Logging;

namespace Kaimo_File_Server.Infrastructure.Notifications;

/// <summary>
/// In-process wake-up for the dispatcher, so an event published by the Web process is
/// handled immediately instead of on the next poll. Other processes only publish; nobody
/// waits on their signal, which is harmless.
/// </summary>
public sealed class NotificationDispatchSignal
{
    private readonly SemaphoreSlim _signal = new(0, 1);

    public void Wake()
    {
        try { _signal.Release(); }
        catch (SemaphoreFullException) { /* already signaled */ }
    }

    /// <summary>Waits for a wake-up or the timeout, whichever comes first.</summary>
    public Task WaitAsync(TimeSpan timeout, CancellationToken ct) => _signal.WaitAsync(timeout, ct);
}

/// <summary>
/// Writes notification events into the outbox table. Best-effort by design (like
/// <c>FileService.AppendChangeAsync</c>): every failure is logged and swallowed so a
/// notification problem never fails the business action that raised the event.
/// </summary>
public sealed class DbNotificationPublisher(
    INotificationRepository repository,
    NotificationDispatchSignal signal,
    ILogger<DbNotificationPublisher> logger,
    TimeProvider? time = null,
    DemoModeOptions? demo = null) : INotificationPublisher
{
    // Rules change rarely and only through the Web UI, so a short-lived cache of "which event
    // types have an enabled rule" keeps the outbox free of events nobody wants.
    private static readonly TimeSpan RuleCacheTtl = TimeSpan.FromSeconds(30);
    private readonly TimeProvider _time = time ?? TimeProvider.System;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private HashSet<string>? _enabledTypes;
    private DateTimeOffset _cacheUntil;

    public async Task PublishAsync(NotificationEvent notification, CancellationToken ct = default)
    {
        // The read-only demo sends no mail and would reject the insert anyway.
        if (demo?.ReadOnly == true) return;

        try
        {
            if (!(await GetEnabledTypesAsync(ct)).Contains(notification.Type))
                return;

            notification.OccurredAtUtc = _time.GetUtcNow().UtcDateTime;
            await repository.AddEventAsync(notification, ct);
            signal.Wake();
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not publish notification event {Type}", notification.Type);
        }
    }

    private async Task<HashSet<string>> GetEnabledTypesAsync(CancellationToken ct)
    {
        if (_enabledTypes is not null && _time.GetUtcNow() < _cacheUntil) return _enabledTypes;
        await _gate.WaitAsync(ct);
        try
        {
            if (_enabledTypes is null || _time.GetUtcNow() >= _cacheUntil)
            {
                _enabledTypes = await repository.GetEnabledRuleTypesAsync(ct);
                _cacheUntil = _time.GetUtcNow() + RuleCacheTtl;
            }
            return _enabledTypes;
        }
        finally { _gate.Release(); }
    }
}

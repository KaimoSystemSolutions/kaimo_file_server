namespace Kaimo_File_Server.Web.Services.Https;

/// <summary>
/// Background service that keeps the self-signed HTTPS certificate fresh. It checks
/// once shortly after startup and then daily whether the certificate has entered its
/// renewal window and, if so, reissues it. Kestrel picks up the new certificate on the
/// next connection (via the <c>ServerCertificateSelector</c>) — no restart needed.
/// </summary>
public sealed class CertificateRenewalService : BackgroundService
{
    private static readonly TimeSpan CheckInterval = TimeSpan.FromDays(1);

    private readonly IHttpsCertificateProvider _provider;
    private readonly TimeProvider _time;
    private readonly ILogger<CertificateRenewalService> _logger;
    private readonly Core.Services.Notifications.INotificationPublisher? _notifications;

    public CertificateRenewalService(
        IHttpsCertificateProvider provider,
        TimeProvider time,
        ILogger<CertificateRenewalService> logger,
        Core.Services.Notifications.INotificationPublisher? notifications = null)
    {
        _notifications = notifications;
        _provider = provider;
        _time = time;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(CheckInterval, _time);
        do
        {
            try
            {
                if (await _provider.EnsureValidAsync(stoppingToken) && _provider.Current is { } renewed)
                    await PublishAsync(Core.Services.Notifications.NotificationEvents.CertificateRenewed(
                        renewed.NotAfter.ToUniversalTime()));
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "HTTPS certificate renewal check failed.");
                await PublishAsync(Core.Services.Notifications.NotificationEvents.CertificateRenewalFailed(ex.Message));
            }
        }
        while (await SafeWaitAsync(timer, stoppingToken));
    }

    private Task PublishAsync(Core.Domain.Notifications.NotificationEvent notification)
        => _notifications?.PublishAsync(notification) ?? Task.CompletedTask;

    private static async Task<bool> SafeWaitAsync(PeriodicTimer timer, CancellationToken ct)
    {
        try
        {
            return await timer.WaitForNextTickAsync(ct);
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }
}

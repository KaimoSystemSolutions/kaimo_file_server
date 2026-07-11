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

    public CertificateRenewalService(
        IHttpsCertificateProvider provider,
        TimeProvider time,
        ILogger<CertificateRenewalService> logger)
    {
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
                await _provider.EnsureValidAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "HTTPS certificate renewal check failed.");
            }
        }
        while (await SafeWaitAsync(timer, stoppingToken));
    }

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

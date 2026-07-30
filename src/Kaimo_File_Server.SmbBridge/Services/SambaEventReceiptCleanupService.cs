using Kaimo_File_Server.Core.Repositories;

namespace Kaimo_File_Server.SmbBridge.Services;

public sealed class SambaEventReceiptCleanupService(
    IServiceScopeFactory scopeFactory,
    IConfiguration configuration,
    ILogger<SambaEventReceiptCleanupService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var retentionDays = Math.Clamp(
            configuration.GetValue("LifecycleEvents:ReceiptRetentionDays", 30),
            1, 365);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var scope = scopeFactory.CreateScope();
                var repository = scope.ServiceProvider
                    .GetRequiredService<ISambaLifecycleEventRepository>();
                var deleted = await repository.DeleteCompletedBeforeAsync(
                    DateTime.UtcNow.AddDays(-retentionDays), stoppingToken);
                if (deleted > 0)
                    logger.LogInformation(
                        "Removed {Count} expired Samba lifecycle event receipts.",
                        deleted);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception error)
            {
                logger.LogError(
                    error, "Samba lifecycle event receipt cleanup failed.");
            }

            await Task.Delay(TimeSpan.FromHours(6), stoppingToken);
        }
    }
}

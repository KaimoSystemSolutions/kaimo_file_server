using Kaimo_File_Server.Core.Services.File;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Kaimo_File_Server.Infrastructure.Services;

/// <summary>
/// Periodically runs <see cref="FileVersionService.ReconcileStorageAsync"/>: moves
/// version blobs from the former application-data store into the pools of their
/// shares, follows shares moved to another pool and removes unneeded copies.
/// Registered in the Host only, so a single process does this work.
/// </summary>
public sealed class VersionStorageReconcilerService(
    IServiceScopeFactory scopeFactory,
    ILogger<VersionStorageReconcilerService> logger) : BackgroundService
{
    // Let startup (migrations, SMB, first requests) settle before the first pass.
    private static readonly TimeSpan InitialDelay = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan Interval = TimeSpan.FromHours(24);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await Task.Delay(InitialDelay, stoppingToken);
            while (true)
            {
                try
                {
                    using var scope = scopeFactory.CreateScope();
                    await scope.ServiceProvider.GetRequiredService<FileVersionService>()
                        .ReconcileStorageAsync(stoppingToken);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    logger.LogError(ex, "Version storage reconciliation failed; retrying on the next run.");
                }

                await Task.Delay(Interval, stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
    }
}

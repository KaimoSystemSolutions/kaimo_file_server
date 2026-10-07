using Kaimo_File_Server.Core.Repositories;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Kaimo_File_Server.Infrastructure.ExternalStorage;

/// <summary>
/// Removes managed SSH known_hosts files left behind by deleted connections (earlier
/// versions did not clean them up for SFTP). Runs once at startup; best-effort, so a
/// failure is logged and never blocks the Web host from starting.
/// </summary>
public sealed class ManagedKnownHostsCleanupHostedService(
    IServiceScopeFactory scopeFactory,
    IRsyncSshSetupService setupService,
    ILogger<ManagedKnownHostsCleanupHostedService> logger) : IHostedService
{
    private static readonly TimeSpan MinimumAge = TimeSpan.FromHours(1);

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var scope = scopeFactory.CreateScope();
            var connections = await scope.ServiceProvider
                .GetRequiredService<IStorageConnectionRepository>()
                .GetAllAsync(cancellationToken);
            int deleted = setupService.DeleteUnreferencedKnownHosts(
                connections.Select(connection => connection.SettingsJson ?? string.Empty), MinimumAge);
            if (deleted > 0)
                logger.LogInformation("Removed {Count} unreferenced managed SSH known_hosts file(s)", deleted);
        }
        catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
        {
            logger.LogWarning(exception, "Cleanup of unreferenced managed SSH known_hosts files failed");
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}

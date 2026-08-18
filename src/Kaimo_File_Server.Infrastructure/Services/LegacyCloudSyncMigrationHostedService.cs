using Kaimo_File_Server.Core.Repositories;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Kaimo_File_Server.Infrastructure.Services;

/// <summary>
/// Completes the additive legacy import once after database migrations and
/// before later registered background services begin scheduled sync work.
/// </summary>
public sealed class LegacyCloudSyncMigrationHostedService(
    IServiceScopeFactory scopeFactory) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        using var scope = scopeFactory.CreateScope();
        await scope.ServiceProvider
            .GetRequiredService<ILegacyCloudSyncMigrationService>()
            .EnsureMigratedAsync(cancellationToken);
    }

    public Task StopAsync(CancellationToken cancellationToken)
        => Task.CompletedTask;
}

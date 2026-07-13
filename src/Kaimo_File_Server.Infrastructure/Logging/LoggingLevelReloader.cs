using Kaimo_File_Server.Core.Logging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Kaimo_File_Server.Infrastructure.Logging;

/// <summary>
/// Polls the cross-process <see cref="ILoggingConfigStore"/> for the global log level
/// and pushes changes into <see cref="LoggingLevelConfigurationSource"/>, which raises
/// the configuration change token so the effective log filter updates live.
///
/// This is what propagates a level change made in the Web UI to the separate SMB host
/// process (they share only the database): the UI writes the flag, this loop turns it
/// into a live filter change within one poll interval — no restart. Mirrors the
/// <c>DataServiceReconciler</c> polling pattern.
/// </summary>
public sealed class LoggingLevelReloader : BackgroundService
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(15);

    private readonly LoggingLevelConfigurationSource _source;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<LoggingLevelReloader> _logger;

    // Last level pushed — avoids raising a change token every tick.
    private string? _lastApplied;

    public LoggingLevelReloader(
        LoggingLevelConfigurationSource source,
        IServiceScopeFactory scopeFactory,
        ILogger<LoggingLevelReloader> logger)
    {
        _source = source;
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            await ApplyOnceAsync();

            try
            {
                await Task.Delay(PollInterval, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    private async Task ApplyOnceAsync()
    {
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var store = scope.ServiceProvider.GetRequiredService<ILoggingConfigStore>();
            var level = await store.GetLevelAsync();

            if (string.Equals(level, _lastApplied, StringComparison.OrdinalIgnoreCase))
                return;

            _source.SetLevel(level);
            _lastApplied = level;
            _logger.LogInformation(LogEvents.LogLevelApplied, LogMessages.LogLevelApplied, level);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(LogEvents.LogLevelReloadFailed, ex, LogMessages.LogLevelReloadFailed);
        }
    }
}

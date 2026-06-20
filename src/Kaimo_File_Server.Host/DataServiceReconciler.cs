using Kaimo_File_Server.Core.Services.DataServices;
using Kaimo_File_Server.Infrastructure.Configuration;

namespace Kaimo_File_Server.Host
{
    /// <summary>
    /// Reconciles the desired state of every <see cref="IManagedDataService"/>
    /// (stored as <c>services.{key}.enabled</c> in the config store) against its
    /// actual runtime status, and writes the observed status back as
    /// <c>services.{key}.status</c>.
    ///
    /// This is how the Web UI controls services living in this host process: the
    /// two run in separate containers and share only the database, so the UI
    /// flips the flag and this loop turns it into Start/Stop calls within a few
    /// seconds. New services (NFS/FTP/…) are reconciled automatically just by
    /// being registered as <see cref="IManagedDataService"/>.
    /// </summary>
    public sealed class DataServiceReconciler : BackgroundService
    {
        private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(5);

        private readonly IEnumerable<IManagedDataService> _services;
        private readonly IServiceScopeFactory _scopeFactory;
        private readonly ILogger<DataServiceReconciler> _logger;

        // Last status we persisted per service — avoids a DB write every tick.
        private readonly Dictionary<string, DataServiceStatus> _lastWritten = new();

        public DataServiceReconciler(
            IEnumerable<IManagedDataService> services,
            IServiceScopeFactory scopeFactory,
            ILogger<DataServiceReconciler> logger)
        {
            _services = services;
            _scopeFactory = scopeFactory;
            _logger = logger;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            _logger.LogInformation(
                "DataServiceReconciler gestartet ({Count} Dienste).", _services.Count());

            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    await ReconcileAllAsync(stoppingToken);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Reconcile-Durchlauf fehlgeschlagen.");
                }

                try
                {
                    await Task.Delay(PollInterval, stoppingToken);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }

            // Graceful shutdown: stop everything that is still running.
            foreach (var service in _services)
            {
                try
                {
                    await service.StopAsync(CancellationToken.None);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex,
                        "Fehler beim Stoppen von '{Key}' während des Shutdowns.", service.Key);
                }
            }
        }

        private async Task ReconcileAllAsync(CancellationToken ct)
        {
            using var scope = _scopeFactory.CreateScope();
            var config = scope.ServiceProvider.GetRequiredService<IConfigRepository>();

            foreach (var service in _services)
            {
                // Default true → backward compatible: SMB keeps running like
                // before until someone explicitly disables it. Read fresh: the
                // flag is written by the Web process, so a cached value here
                // would hide the change for up to the cache TTL.
                var desired = await config.GetFreshAsync(
                    DataServiceKeys.EnabledKey(service.Key), fallback: true);

                var running = service.Status == DataServiceStatus.Running;

                if (desired && !running)
                {
                    _logger.LogInformation("Starte Dienst '{Key}'…", service.Key);
                    await service.StartAsync(ct);
                }
                else if (!desired && running)
                {
                    _logger.LogInformation("Stoppe Dienst '{Key}'…", service.Key);
                    await service.StopAsync(ct);
                }

                await PersistStatusIfChangedAsync(config, service);
            }
        }

        private async Task PersistStatusIfChangedAsync(IConfigRepository config, IManagedDataService service)
        {
            if (_lastWritten.TryGetValue(service.Key, out var last) && last == service.Status)
                return;

            await config.SetAsync(DataServiceKeys.StatusKey(service.Key), service.Status.ToString());
            _lastWritten[service.Key] = service.Status;
        }
    }
}

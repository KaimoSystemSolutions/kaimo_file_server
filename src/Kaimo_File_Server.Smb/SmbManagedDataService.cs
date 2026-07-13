using Kaimo_File_Server.Core.Logging;
using Kaimo_File_Server.Core.Services.DataServices;
using Microsoft.Extensions.Logging;

namespace Kaimo_File_Server.Smb
{
    /// <summary>
    /// Adapts the <see cref="SmbServer"/> to the transport-agnostic
    /// <see cref="IManagedDataService"/> contract so the host reconciler can
    /// start/stop it without knowing it is SMB. Start/Stop on SmbServer are
    /// synchronous and idempotent, so this wrapper just tracks the reported
    /// status around them.
    /// </summary>
    public sealed class SmbManagedDataService : IManagedDataService
    {
        private readonly SmbServer _server;
        private readonly ILogger<SmbManagedDataService> _logger;

        public SmbManagedDataService(SmbServer server, ILogger<SmbManagedDataService> logger)
        {
            _server = server;
            _logger = logger;
        }

        public string Key => "smb";

        public string DisplayName => "SMB / CIFS";

        public DataServiceStatus Status { get; private set; } = DataServiceStatus.Stopped;

        public Task StartAsync(CancellationToken ct)
        {
            if (_server.IsRunning)
            {
                Status = DataServiceStatus.Running;
                return Task.CompletedTask;
            }

            try
            {
                Status = DataServiceStatus.Starting;
                _server.Start();
                Status = DataServiceStatus.Running;
            }
            catch (Exception ex)
            {
                Status = DataServiceStatus.Faulted;
                _logger.LogError(LogEvents.SmbServiceStartFailed, ex, LogMessages.SmbServiceStartFailed);
                throw;
            }

            return Task.CompletedTask;
        }

        public Task StopAsync(CancellationToken ct)
        {
            if (!_server.IsRunning)
            {
                Status = DataServiceStatus.Stopped;
                return Task.CompletedTask;
            }

            try
            {
                Status = DataServiceStatus.Stopping;
                _server.Stop();
                Status = DataServiceStatus.Stopped;
            }
            catch (Exception ex)
            {
                Status = DataServiceStatus.Faulted;
                _logger.LogError(LogEvents.SmbServiceStopFailed, ex, LogMessages.SmbServiceStopFailed);
                throw;
            }

            return Task.CompletedTask;
        }
    }
}

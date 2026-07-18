using Kaimo_File_Server.Core.Services.DataServices;

namespace Kaimo_File_Server.Host
{
    /// <summary>
    /// Post-Phase-5 replacement for the removed in-process <c>SmbManagedDataService</c>.
    /// Since the cutover, <c>smbd</c> runs in its own container (<c>kaimo_samba</c>)
    /// and the actual on/off enforcement lives in the bridge's
    /// <c>AuthorizeConnect</c> (deny-all when <c>services.smb.enabled=false</c>), which
    /// is what makes a disabled service reject every TREE_CONNECT.
    ///
    /// This adapter keeps the "Datendienste" tab meaningful without any UI change:
    /// the host reconciler still flips Start/Stop from the same
    /// <c>services.smb.enabled</c> flag and persists the observed status, so the UI
    /// status column keeps tracking the toggle. There is no in-process SMB server to
    /// start anymore — Start/Stop only reflect the desired state as status.
    /// </summary>
    public sealed class SambaSmbControlService : IManagedDataService
    {
        private readonly ILogger<SambaSmbControlService> _logger;

        public SambaSmbControlService(ILogger<SambaSmbControlService> logger)
        {
            _logger = logger;
        }

        public string Key => "smb";

        public string DisplayName => "SMB / CIFS";

        public DataServiceStatus Status { get; private set; } = DataServiceStatus.Stopped;

        public Task StartAsync(CancellationToken ct)
        {
            if (Status != DataServiceStatus.Running)
                _logger.LogInformation(
                    "SMB enabled — smbd runs in the kaimo_samba container; access is gated by the bridge connect hook.");
            Status = DataServiceStatus.Running;
            return Task.CompletedTask;
        }

        public Task StopAsync(CancellationToken ct)
        {
            if (Status == DataServiceStatus.Running)
                _logger.LogInformation(
                    "SMB disabled — the bridge now denies every TREE_CONNECT (services.smb.enabled=false).");
            Status = DataServiceStatus.Stopped;
            return Task.CompletedTask;
        }
    }
}

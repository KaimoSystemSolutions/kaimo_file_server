namespace Kaimo_File_Server.Core.Services.DataServices;

/// <summary>
/// Runtime state of a managed data service (SMB, future NFS/FTP, …).
/// </summary>
public enum DataServiceStatus
{
    Stopped,
    Starting,
    Running,
    Stopping,
    Faulted
}

/// <summary>
/// A data service that can be started and stopped at runtime — e.g. the SMB
/// transport. Implementations live in their own assembly (the SMB transport is
/// already a separate DLL) so the host only depends on this abstraction. This
/// is the seam along which transports can later be loaded as plug-in DLLs.
///
/// Desired state and reported status are exchanged across process boundaries
/// (the Web UI and the SMB host run in separate containers) purely through the
/// config store — see <see cref="DataServiceKeys"/>. A reconciler running in
/// the host process turns the desired-state flag into Start/Stop calls.
/// </summary>
public interface IManagedDataService
{
    /// <summary>Stable identifier used in config keys, e.g. "smb".</summary>
    string Key { get; }

    /// <summary>Human-readable name for the UI, e.g. "SMB / CIFS".</summary>
    string DisplayName { get; }

    /// <summary>Current runtime status.</summary>
    DataServiceStatus Status { get; }

    Task StartAsync(CancellationToken ct);
    Task StopAsync(CancellationToken ct);
}

/// <summary>
/// Central conventions for the config keys used to coordinate data services
/// between the Web UI (writes the desired-state flag) and the host reconciler
/// (reads the flag, writes back the actual status).
/// </summary>
public static class DataServiceKeys
{
    /// <summary>Bool flag: whether the service should be running.</summary>
    public static string EnabledKey(string serviceKey) => $"services.{serviceKey}.enabled";

    /// <summary>String: last reported <see cref="DataServiceStatus"/>.</summary>
    public static string StatusKey(string serviceKey) => $"services.{serviceKey}.status";
}

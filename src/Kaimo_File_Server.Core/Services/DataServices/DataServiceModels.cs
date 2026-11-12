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

    /// <summary>
    /// UTC timestamp the Host reconciler refreshes periodically. A reported status is only
    /// trustworthy while this is recent; once it goes stale the Host is not running.
    /// </summary>
    public const string HostHeartbeatKey = "runtime.host.heartbeat";

    /// <summary>How often the Host refreshes <see cref="HostHeartbeatKey"/>.</summary>
    public static readonly TimeSpan HostHeartbeatInterval = TimeSpan.FromSeconds(30);

    /// <summary>Age after which the Host counts as unreachable (three missed heartbeats).</summary>
    public static readonly TimeSpan HostHeartbeatStaleAfter = TimeSpan.FromSeconds(90);
}

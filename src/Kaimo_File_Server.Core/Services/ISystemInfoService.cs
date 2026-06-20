namespace Kaimo_File_Server.Core.Services;

/// <summary>
/// Provides read-only runtime information about the host the application runs on:
/// network addresses, storage usage of the data drive and the current process's
/// memory consumption. Used by the settings page for the IP / storage / RAM tabs.
/// </summary>
public interface ISystemInfoService
{
    /// <summary>The machine's host name.</summary>
    string HostName { get; }

    /// <summary>All non-loopback IPv4/IPv6 addresses of the host's active interfaces.</summary>
    IReadOnlyList<NetworkAddressInfo> GetNetworkAddresses();

    /// <summary>
    /// The server's public/global IP as seen from the internet, or <c>null</c> when
    /// it can't be determined (no outbound access / service unreachable). Requires an
    /// outbound HTTPS call, so this is intentionally separate from the local lookups.
    /// </summary>
    Task<string?> GetPublicIpAsync(CancellationToken ct = default);

    /// <summary>Total/used/free space of the drive that hosts the storage root.</summary>
    StorageUsageInfo GetStorageUsage();

    /// <summary>Memory currently used by this (the running tool's) process.</summary>
    MemoryUsageInfo GetMemoryUsage();
}

/// <summary>A single network address bound to an interface.</summary>
public record NetworkAddressInfo(string InterfaceName, string Address, string Family);

/// <summary>Disk usage of the drive that holds the file storage.</summary>
public record StorageUsageInfo(
    string StoragePath,
    string DriveName,
    long TotalBytes,
    long UsedBytes,
    long FreeBytes,
    bool Available)
{
    public double UsedPercent => TotalBytes > 0 ? (double)UsedBytes / TotalBytes * 100d : 0d;
}

/// <summary>Memory consumption of the current process.</summary>
public record MemoryUsageInfo(
    long WorkingSetBytes,
    long PrivateBytes,
    long ManagedHeapBytes,
    long TotalAvailableBytes)
{
    public double WorkingSetPercent =>
        TotalAvailableBytes > 0 ? (double)WorkingSetBytes / TotalAvailableBytes * 100d : 0d;
}

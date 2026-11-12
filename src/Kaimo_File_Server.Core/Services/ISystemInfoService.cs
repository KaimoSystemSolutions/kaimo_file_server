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
    List<StorageUsageInfo> GetStorageUsage();

    /// <summary>Memory currently used by this (the running tool's) process.</summary>
    MemoryUsageInfo GetMemoryUsage();
}

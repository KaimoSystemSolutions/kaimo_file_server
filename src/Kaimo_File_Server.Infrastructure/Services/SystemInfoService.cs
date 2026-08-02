using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using Kaimo_File_Server.Core.Services;

namespace Kaimo_File_Server.Infrastructure.Services;

/// <summary>
/// Gathers live host/runtime information by querying the OS and the current
/// process. Stateless and cheap; registered as a singleton with the configured
/// storage root so the storage tab reports the data drive's usage.
/// </summary>
public class SystemInfoService : ISystemInfoService
{
    private IReadOnlyList<string> _storagePoolPaths;

    // Shared, short-timeout client for the public-IP lookup. Static so the
    // singleton service doesn't churn sockets.
    private static readonly HttpClient PublicIpClient = new()
    {
        Timeout = TimeSpan.FromSeconds(4),
    };

    public SystemInfoService(IReadOnlyList<string> storagePaths)
    {
        _storagePoolPaths = storagePaths;
    }

    public string HostName
    {
        get
        {
            try { return Dns.GetHostName(); }
            catch { return Environment.MachineName; }
        }
    }

    public IReadOnlyList<NetworkAddressInfo> GetNetworkAddresses()
    {
        var result = new List<NetworkAddressInfo>();

        try
        {
            foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (nic.OperationalStatus != OperationalStatus.Up) continue;
                if (nic.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;

                foreach (var unicast in nic.GetIPProperties().UnicastAddresses)
                {
                    var family = unicast.Address.AddressFamily;
                    if (family != AddressFamily.InterNetwork &&
                        family != AddressFamily.InterNetworkV6) continue;
                    if (IPAddress.IsLoopback(unicast.Address)) continue;

                    var label = family == AddressFamily.InterNetwork ? "IPv4" : "IPv6";
                    result.Add(new NetworkAddressInfo(nic.Name, unicast.Address.ToString(), label));
                }
            }
        }
        catch
        {
            // Network enumeration can fail in locked-down containers — return what we have.
        }

        // IPv4 first, then by interface name, for a stable, readable list.
        return result
            .OrderByDescending(a => a.Family == "IPv4")
            .ThenBy(a => a.InterfaceName)
            .ToList();
    }

    public async Task<string?> GetPublicIpAsync(CancellationToken ct = default)
    {
        // Plain-text echo endpoint that simply returns the caller's public IP.
        try
        {
            var ip = (await PublicIpClient.GetStringAsync("https://api.ipify.org", ct)).Trim();
            return string.IsNullOrWhiteSpace(ip) ? null : ip;
        }
        catch
        {
            return null;
        }
    }

    public List<StorageUsageInfo> GetStorageUsage()
    {
        var result = new List<StorageUsageInfo>();

        try
        {
            foreach (string storagePoolPath in _storagePoolPaths) {

                // Fall back to the application's own location when the configured
                // storage path doesn't exist yet (e.g. on a dev machine).
                var probe = Directory.Exists(storagePoolPath) ? storagePoolPath : AppContext.BaseDirectory;
                var root = Path.GetPathRoot(Path.GetFullPath(probe));
                if (string.IsNullOrEmpty(root)) root = probe;

                var drive = new DriveInfo(root);
                var total = drive.TotalSize;
                var free = drive.TotalFreeSpace;

                result.Add(new StorageUsageInfo(
                    StoragePath: storagePoolPath,
                    DriveName: drive.Name,
                    TotalBytes: total,
                    UsedBytes: total - free,
                    FreeBytes: free,
                    Available: true));
            }
            return result;
        }
        catch
        {
            return result;
        }
    }

    public MemoryUsageInfo GetMemoryUsage()
    {
        using var process = Process.GetCurrentProcess();
        process.Refresh();

        var gc = GC.GetGCMemoryInfo();

        return new MemoryUsageInfo(
            WorkingSetBytes: process.WorkingSet64,
            PrivateBytes: process.PrivateMemorySize64,
            // A non-collecting GetTotalMemory call includes dead objects that the GC
            // has not reclaimed yet. That made every Blazor refresh appear to leak.
            // HeapSizeBytes is the stable heap snapshot captured by the last GC and
            // avoids both that false signal and an expensive forced collection.
            ManagedHeapBytes: gc.HeapSizeBytes > 0
                ? gc.HeapSizeBytes
                : GC.GetTotalMemory(forceFullCollection: false),
            TotalAvailableBytes: gc.TotalAvailableMemoryBytes);
    }
}

namespace Kaimo_File_Server.Core.Services;

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
    /// <summary>
    /// Identifies the filesystem the pool lives on (Linux <c>major:minor</c>, else the volume
    /// root). Pools with the same id share one disk and therefore one used/free figure.
    /// <c>null</c> = unknown, the pool is treated as being alone on its disk.
    /// </summary>
    public string? DeviceId { get; init; }

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

using System.Buffers.Binary;
using System.Runtime.InteropServices;

namespace Kaimo_File_Server.Infrastructure.Storage;

/// <summary>Result of <see cref="FileLinkStatus.TryGet"/>; describes the entry itself, never a link target.</summary>
internal readonly record struct LinkStatus(
    bool IsDirectory, bool IsSymlink, long CtimeTicks, long SizeBytes, ulong Device);

/// <summary>
/// <c>lstat</c>-style status of a path via statx(2) with <c>AT_SYMLINK_NOFOLLOW</c>: the inode
/// change time (which a rename into the recycle bin updates), the size and the device, so callers
/// can detect replaced entries and stay on one file system. On non-Linux hosts (tests) the last
/// write time stands in for the change time and the device is 0.
/// </summary>
internal static class FileLinkStatus
{
    private const int AtFdCwd = -100;
    private const int AtSymlinkNoFollow = 0x100;
    private const uint StatxBasicStats = 0x7ff;
    private const int StatxSize = 256;      // sizeof(struct statx)
    private const int ModeOffset = 28;      // stx_mode (u16)
    private const int SizeOffset = 40;      // stx_size (u64)
    private const int CtimeOffset = 96;     // stx_ctime (statx_timestamp)
    private const int DevMajorOffset = 136; // stx_dev_major, stx_dev_minor (u32 each)
    private const int FileTypeMask = 0xF000;
    private const int DirectoryType = 0x4000;
    private const int SymlinkType = 0xA000;

    private static bool _nativeUnavailable = !OperatingSystem.IsLinux();

    /// <summary>The status of <paramref name="path"/>, or <c>null</c> when it does not exist.</summary>
    public static LinkStatus? TryGet(string path)
    {
        if (!_nativeUnavailable)
        {
            try
            {
                var buf = new byte[StatxSize];
                if (Statx(AtFdCwd, path, AtSymlinkNoFollow, StatxBasicStats, buf) != 0)
                    return null;
                var type = BinaryPrimitives.ReadUInt16LittleEndian(buf.AsSpan(ModeOffset)) & FileTypeMask;
                var sec = BinaryPrimitives.ReadInt64LittleEndian(buf.AsSpan(CtimeOffset));
                var nsec = BinaryPrimitives.ReadUInt32LittleEndian(buf.AsSpan(CtimeOffset + 8));
                var device = ((ulong)BinaryPrimitives.ReadUInt32LittleEndian(buf.AsSpan(DevMajorOffset)) << 32)
                             | BinaryPrimitives.ReadUInt32LittleEndian(buf.AsSpan(DevMajorOffset + 4));
                return new LinkStatus(
                    type == DirectoryType,
                    type == SymlinkType,
                    DateTime.UnixEpoch.Ticks + sec * TimeSpan.TicksPerSecond + nsec / 100,
                    type is DirectoryType or SymlinkType ? 0 : (long)BinaryPrimitives.ReadUInt64LittleEndian(buf.AsSpan(SizeOffset)),
                    device);
            }
            catch (Exception ex) when (ex is EntryPointNotFoundException or DllNotFoundException)
            {
                _nativeUnavailable = true; // libc without statx (glibc < 2.28)
            }
        }

        FileSystemInfo info = Directory.Exists(path) ? new DirectoryInfo(path) : new FileInfo(path);
        if (!info.Exists && info.LinkTarget is null)
            return null;
        var isLink = info.Attributes.HasFlag(FileAttributes.ReparsePoint);
        var isDirectory = !isLink && info is DirectoryInfo;
        return new LinkStatus(
            isDirectory, isLink, info.LastWriteTimeUtc.Ticks,
            isDirectory || isLink ? 0 : ((FileInfo)info).Length, 0);
    }

    [DllImport("libc", EntryPoint = "statx", SetLastError = true)]
    private static extern int Statx(
        int dirfd, [MarshalAs(UnmanagedType.LPUTF8Str)] string path, int flags, uint mask, byte[] buffer);
}

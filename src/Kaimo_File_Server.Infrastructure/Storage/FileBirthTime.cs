using System.Buffers.Binary;
using System.Runtime.InteropServices;

namespace Kaimo_File_Server.Infrastructure.Storage;

/// <summary>
/// Reads the creation time of a file or directory the same way the SMB side reports it,
/// so the web UI and Windows Explorer show identical values:
/// <list type="number">
/// <item>The create time Samba keeps in the <c>user.DOSATTRIB</c> xattr (written on SMB create,
/// or when a client sets the creation time explicitly).</item>
/// <item>The real birth time via statx(2). The kaimo_bridge VFS module feeds the same value to
/// Samba (kaimo_fill_btime).</item>
/// <item>.NET's <see cref="FileSystemInfo.CreationTimeUtc"/> — on Linux only min(mtime, ctime),
/// used when the file system records no birth time (e.g. NFS, CIFS) and on non-Linux hosts.</item>
/// </list>
/// </summary>
internal static class FileBirthTime
{
    private const int AtFdCwd = -100;
    private const uint StatxBtime = 0x800;
    private const int StatxSize = 256;      // sizeof(struct statx)
    private const int BtimeOffset = 80;     // offsetof(struct statx, stx_btime)
    private const int DosAttribMaxSize = 256;

    private static bool _nativeUnavailable = !OperatingSystem.IsLinux();

    public static DateTime GetUtc(FileSystemInfo info)
    {
        if (!_nativeUnavailable)
        {
            try
            {
                var buf = new byte[Math.Max(StatxSize, DosAttribMaxSize)];
                var len = GetXattr(info.FullName, "user.DOSATTRIB", buf, DosAttribMaxSize);
                if (len > 0 && TryParseDosAttribCreateTime(buf.AsSpan(0, (int)len), out var dosCreated))
                    return dosCreated;

                Array.Clear(buf);
                if (Statx(AtFdCwd, info.FullName, 0, StatxBtime, buf) == 0 &&
                    (BinaryPrimitives.ReadUInt32LittleEndian(buf) & StatxBtime) != 0)
                {
                    var sec = BinaryPrimitives.ReadInt64LittleEndian(buf.AsSpan(BtimeOffset));
                    var nsec = BinaryPrimitives.ReadUInt32LittleEndian(buf.AsSpan(BtimeOffset + 8));
                    return DateTime.UnixEpoch.AddSeconds(sec).AddTicks(nsec / 100);
                }
            }
            catch (Exception ex) when (ex is EntryPointNotFoundException or DllNotFoundException)
            {
                _nativeUnavailable = true; // libc without statx (glibc < 2.28)
            }
        }

        return info.CreationTimeUtc;
    }

    /// <summary>
    /// Extracts the create time from Samba's NDR-encoded <c>xattr_DOSATTRIB</c> blob
    /// (librpc/idl/xattr.idl): NUL-terminated attrib hex string, align 2, uint16 version,
    /// uint16 union level, align 4, then xattr_DosInfo3/4/5. Same rules as smbd's
    /// parse_dos_attribute_blob: only v3–v5, only with XATTR_DOSINFO_CREATE_TIME set and non-null.
    /// </summary>
    internal static bool TryParseDosAttribCreateTime(ReadOnlySpan<byte> blob, out DateTime utc)
    {
        utc = default;
        var nul = blob.IndexOf((byte)0);
        if (nul < 0) return false;

        var pos = (nul + 2) & ~1;                 // skip attrib_hex + NUL, align 2
        if (blob.Length < pos + 4) return false;  // legacy blob: attrib hex string only
        var version = BinaryPrimitives.ReadUInt16LittleEndian(blob[pos..]);
        pos = (pos + 4 + 3) & ~3;                 // version + union level, align 4

        var createOffset = version switch { 3 => 28, 4 => 16, 5 => 8, _ => -1 };
        if (createOffset < 0 || blob.Length < pos + createOffset + 8) return false;
        if ((BinaryPrimitives.ReadUInt32LittleEndian(blob[pos..]) & 0x10) == 0) return false; // XATTR_DOSINFO_CREATE_TIME

        var ntTime = BinaryPrimitives.ReadInt64LittleEndian(blob[(pos + createOffset)..]);
        if (ntTime <= 0 || ntTime > DateTime.MaxValue.ToFileTimeUtc()) return false;
        utc = DateTime.FromFileTimeUtc(ntTime);
        return true;
    }

    [DllImport("libc", EntryPoint = "statx", SetLastError = true)]
    private static extern int Statx(
        int dirfd, [MarshalAs(UnmanagedType.LPUTF8Str)] string path, int flags, uint mask, byte[] buffer);

    [DllImport("libc", EntryPoint = "getxattr", SetLastError = true)]
    private static extern nint GetXattr(
        [MarshalAs(UnmanagedType.LPUTF8Str)] string path, [MarshalAs(UnmanagedType.LPUTF8Str)] string name,
        byte[] value, nuint size);
}

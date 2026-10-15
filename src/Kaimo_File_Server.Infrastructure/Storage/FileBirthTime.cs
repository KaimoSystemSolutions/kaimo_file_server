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
    private const string DosAttribName = "user.DOSATTRIB";
    private const uint AttribFlag = 0x01;      // XATTR_DOSINFO_ATTRIB
    private const uint CreateTimeFlag = 0x10;  // XATTR_DOSINFO_CREATE_TIME

    private static bool _nativeUnavailable = !OperatingSystem.IsLinux();

    public static DateTime GetUtc(FileSystemInfo info)
        => TryGetRecordedUtc(info, out var utc) ? utc : info.CreationTimeUtc;

    /// <summary>
    /// Call before changing the modification time. Without a recorded creation time (no birth time
    /// on the file system, nothing in <c>user.DOSATTRIB</c>) Linux derives it from min(mtime, ctime),
    /// so a back-dated mtime would move it as well; the current value is stored first, the way
    /// smbd records the create time of every file it creates.
    /// </summary>
    public static void PinBeforeTimeChange(FileSystemInfo info)
    {
        if (OperatingSystem.IsLinux() && !TryGetRecordedUtc(info, out _))
            TrySetUtc(info, info.CreationTimeUtc);
    }

    /// <summary>The create time from <c>user.DOSATTRIB</c>, else the statx birth time; false when neither exists.</summary>
    private static bool TryGetRecordedUtc(FileSystemInfo info, out DateTime utc)
    {
        utc = default;
        if (_nativeUnavailable) return false;
        try
        {
            var buf = new byte[Math.Max(StatxSize, DosAttribMaxSize)];
            var len = GetXattr(info.FullName, "user.DOSATTRIB", buf, DosAttribMaxSize);
            if (len > 0 && TryParseDosAttribCreateTime(buf.AsSpan(0, (int)len), out utc))
                return true;

            Array.Clear(buf);
            if (Statx(AtFdCwd, info.FullName, 0, StatxBtime, buf) == 0 &&
                (BinaryPrimitives.ReadUInt32LittleEndian(buf) & StatxBtime) != 0)
            {
                var sec = BinaryPrimitives.ReadInt64LittleEndian(buf.AsSpan(BtimeOffset));
                var nsec = BinaryPrimitives.ReadUInt32LittleEndian(buf.AsSpan(BtimeOffset + 8));
                utc = DateTime.UnixEpoch.AddSeconds(sec).AddTicks(nsec / 100);
                return true;
            }
        }
        catch (Exception ex) when (IsNativeMissing(ex))
        {
            _nativeUnavailable = true; // libc without statx (glibc < 2.28)
        }
        return false;
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
        if (!TryLocateDosInfo(blob, out var pos, out var createOffset)) return false;
        if ((BinaryPrimitives.ReadUInt32LittleEndian(blob[pos..]) & CreateTimeFlag) == 0) return false;

        var ntTime = BinaryPrimitives.ReadInt64LittleEndian(blob[(pos + createOffset)..]);
        if (ntTime <= 0 || ntTime > DateTime.MaxValue.ToFileTimeUtc()) return false;
        utc = DateTime.FromFileTimeUtc(ntTime);
        return true;
    }

    /// <summary>
    /// Sets the creation time so that <see cref="GetUtc"/> (and Samba) read it back. On Linux, where no
    /// syscall can set the birth time, it goes into the create time of <c>user.DOSATTRIB</c>, keeping
    /// any DOS attributes already stored there. Best effort: returns <c>false</c> when the file system
    /// does not support user xattrs.
    /// </summary>
    public static bool TrySetUtc(FileSystemInfo info, DateTime utc)
    {
        utc = utc.ToUniversalTime();
        if (!OperatingSystem.IsLinux())
        {
            info.CreationTimeUtc = utc;
            return true;
        }

        try
        {
            var buf = new byte[DosAttribMaxSize];
            var len = GetXattr(info.FullName, DosAttribName, buf, DosAttribMaxSize);
            var blob = BuildOrPatchDosAttrib(
                len > 0 ? buf.AsSpan(0, (int)len) : ReadOnlySpan<byte>.Empty, info is DirectoryInfo, utc);
            return SetXattr(info.FullName, DosAttribName, blob, (nuint)blob.Length, 0) == 0;
        }
        catch (Exception ex) when (IsNativeMissing(ex))
        {
            return false;
        }
    }

    /// <summary>A libc without the xattr calls must not fail the write that keeps the times.</summary>
    private static bool IsNativeMissing(Exception ex) => ex is EntryPointNotFoundException or DllNotFoundException;

    /// <summary>
    /// Carries the creation time of <paramref name="replacedPath"/> over to <paramref name="replacementPath"/>
    /// before the latter replaces it. A Linux rename publishes a new inode, so without this every
    /// overwrite would reset the creation time (Windows <c>ReplaceFile</c> keeps it by itself).
    /// </summary>
    public static void PreserveOnReplace(string replacedPath, string replacementPath)
    {
        if (!OperatingSystem.IsLinux()) return;

        try
        {
            var buf = new byte[DosAttribMaxSize];
            var len = GetXattr(replacedPath, DosAttribName, buf, DosAttribMaxSize);
            if (len > 0)
                SetXattr(replacementPath, DosAttribName, buf, (nuint)len, 0);
            else
                TrySetUtc(new FileInfo(replacementPath), GetUtc(new FileInfo(replacedPath)));
        }
        catch (Exception ex) when (IsNativeMissing(ex))
        {
            // The overwrite proceeds; only the creation time is not carried over.
        }
    }

    /// <summary>
    /// Returns <paramref name="existing"/> with its create time set to <paramref name="utc"/> when it
    /// is a v3–v5 blob (DOS attributes unchanged), otherwise a new v5 blob that keeps the attributes
    /// of a legacy hex-only blob or defaults to ARCHIVE (file) / DIRECTORY (folder), as smbd would.
    /// </summary>
    internal static byte[] BuildOrPatchDosAttrib(ReadOnlySpan<byte> existing, bool isDirectory, DateTime utc)
    {
        var ntTime = utc.ToUniversalTime().ToFileTimeUtc();

        if (TryLocateDosInfo(existing, out var pos, out var createOffset))
        {
            var patched = existing.ToArray();
            var flags = BinaryPrimitives.ReadUInt32LittleEndian(patched.AsSpan(pos));
            BinaryPrimitives.WriteUInt32LittleEndian(patched.AsSpan(pos), flags | CreateTimeFlag);
            BinaryPrimitives.WriteInt64LittleEndian(patched.AsSpan(pos + createOffset), ntTime);
            return patched;
        }

        var attrib = TryParseLegacyAttrib(existing, out var legacy) ? legacy : isDirectory ? 0x10u : 0x20u;
        var hex = System.Text.Encoding.ASCII.GetBytes($"0x{attrib:x}");
        var infoPos = (((hex.Length + 2) & ~1) + 4 + 3) & ~3; // hex + NUL, align 2, version + level, align 4
        var blob = new byte[infoPos + 16];                   // xattr_DosInfo5: flags, attrib, create_time
        hex.CopyTo(blob, 0);
        var versionPos = (hex.Length + 2) & ~1;
        BinaryPrimitives.WriteUInt16LittleEndian(blob.AsSpan(versionPos), 5);
        BinaryPrimitives.WriteUInt16LittleEndian(blob.AsSpan(versionPos + 2), 5);
        BinaryPrimitives.WriteUInt32LittleEndian(blob.AsSpan(infoPos), AttribFlag | CreateTimeFlag);
        BinaryPrimitives.WriteUInt32LittleEndian(blob.AsSpan(infoPos + 4), attrib);
        BinaryPrimitives.WriteInt64LittleEndian(blob.AsSpan(infoPos + 8), ntTime);
        return blob;
    }

    /// <summary>
    /// Finds the xattr_DosInfo3/4/5 struct in an NDR <c>xattr_DOSATTRIB</c> blob (librpc/idl/xattr.idl):
    /// NUL-terminated attrib hex string, align 2, uint16 version, uint16 union level, align 4.
    /// </summary>
    private static bool TryLocateDosInfo(ReadOnlySpan<byte> blob, out int pos, out int createOffset)
    {
        pos = createOffset = -1;
        var nul = blob.IndexOf((byte)0);
        if (nul < 0) return false;

        pos = (nul + 2) & ~1;                     // skip attrib_hex + NUL, align 2
        if (blob.Length < pos + 4) return false;  // legacy blob: attrib hex string only
        var version = BinaryPrimitives.ReadUInt16LittleEndian(blob[pos..]);
        pos = (pos + 4 + 3) & ~3;                 // version + union level, align 4

        createOffset = version switch { 3 => 28, 4 => 16, 5 => 8, _ => -1 };
        return createOffset >= 0 && blob.Length >= pos + createOffset + 8;
    }

    private static bool TryParseLegacyAttrib(ReadOnlySpan<byte> blob, out uint attrib)
    {
        attrib = 0;
        var nul = blob.IndexOf((byte)0);
        var text = System.Text.Encoding.ASCII.GetString(nul >= 0 ? blob[..nul] : blob);
        return text.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
            && uint.TryParse(text.AsSpan(2), System.Globalization.NumberStyles.HexNumber, null, out attrib);
    }

    [DllImport("libc", EntryPoint = "setxattr", SetLastError = true)]
    private static extern int SetXattr(
        [MarshalAs(UnmanagedType.LPUTF8Str)] string path, [MarshalAs(UnmanagedType.LPUTF8Str)] string name,
        byte[] value, nuint size, int flags);

    [DllImport("libc", EntryPoint = "statx", SetLastError = true)]
    private static extern int Statx(
        int dirfd, [MarshalAs(UnmanagedType.LPUTF8Str)] string path, int flags, uint mask, byte[] buffer);

    [DllImport("libc", EntryPoint = "getxattr", SetLastError = true)]
    private static extern nint GetXattr(
        [MarshalAs(UnmanagedType.LPUTF8Str)] string path, [MarshalAs(UnmanagedType.LPUTF8Str)] string name,
        byte[] value, nuint size);
}

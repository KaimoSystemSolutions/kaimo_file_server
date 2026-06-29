using Kaimo_File_Server.Core.Domain;
using Kaimo_File_Server.Core.Domain.Identity;
using Kaimo_File_Server.Core.Helpers;
using Kaimo_File_Server.Core.Services.File;
using Kaimo_File_Server.Core.Storage;
using Smb.FileSystem;
using Smb.FileSystem.Versioning;
using Smb.Protocol.Enums;

namespace Kaimo_File_Server.Smb;

/// <summary>
/// The SMB file backend for one share. Bridges the library's <see cref="IFileStore"/> to Kaimo's
/// <see cref="IFileService"/>, preserving the original per-user / per-path ACL enforcement: every
/// operation runs as the authenticated <see cref="UserContext"/> and the ACL checks live inside
/// <c>IFileService</c>.
///
/// The library's <see cref="IFileStore"/> methods carry no identity. For CREATE (and snapshot
/// enumeration) the user comes from the ambient <see cref="SmbCaller"/> the server sets per request;
/// every other operation uses the user captured by the open <see cref="IFileSession"/>.
///
/// Also implements <see cref="ISnapshotStore"/> (Windows "Previous Versions" / FSCTL enumerate) and
/// <see cref="IVolumeInfoProvider"/> (real volume label / free space).
/// </summary>
internal sealed class KaimoFileStore : IFileStore, ISnapshotStore, IVolumeInfoProvider
{
    private readonly Guid _shareId;
    private readonly IFileService _fileService;

    public KaimoFileStore(Guid shareId, IFileService fileService)
    {
        _shareId = shareId;
        _fileService = fileService;
    }

    // ───────────────────────── CREATE ─────────────────────────

    public FileStoreResult<IFileHandle> Create(
        string path, FileAccessIntent access, CreateDispositionIntent disposition,
        bool directoryRequired, bool nonDirectoryRequired, out CreateOutcome createAction)
    {
        createAction = CreateOutcome.Opened;

        if (!TryGetUser(out UserContext user))
            return Fail(NtStatus.AccessDenied);

        try
        {
            // Snapshot path (@GMT-…\real\path) → previous version, read-only.
            if (GmtToken.TrySplitSnapshotPath(path, out DateTime snapAt, out string realPath))
                return OpenSnapshot(realPath, snapAt, user);

            string relative = ShareRelativePath.Normalize(path);

            if (directoryRequired)
                return CreateDirectory(relative, disposition, user, out createAction);

            FileOpenResult result = SmbSync.Run(() => _fileService.OpenAsync(
                relative, MapDisposition(disposition), MapAccess(access), AllShare, user));

            if (nonDirectoryRequired && result.Session.IsDirectory)
            {
                SmbSync.Run(() => result.Session.DisposeAsync());
                return Fail(NtStatus.FileIsADirectory);
            }

            createAction = MapOutcome(result.Status);
            return Ok(new KaimoFileHandle(_fileService, result.Session));
        }
        catch (UnauthorizedAccessException) { return Fail(NtStatus.AccessDenied); }
        catch (FileNotFoundException) { return Fail(NtStatus.ObjectNameNotFound); }
        catch (DirectoryNotFoundException) { return Fail(NtStatus.ObjectPathNotFound); }
        catch (IOException ex) when ((ex.HResult & 0xFFFF) == 32) { return Fail(NtStatus.SharingViolation); }
        catch (IOException) when (disposition == CreateDispositionIntent.Create) { return Fail(NtStatus.ObjectNameCollision); }
        catch (Exception ex)
        {
            Console.WriteLine($"[CreateFile ERROR] {path}: {ex.Message}");
            return Fail(NtStatus.AccessDenied);
        }
    }

    private FileStoreResult<IFileHandle> OpenSnapshot(string realPath, DateTime snapAt, UserContext user)
    {
        // Snapshot root (no real path) → open the live share root read-only as a directory.
        if (string.IsNullOrEmpty(realPath))
        {
            FileOpenResult root = SmbSync.Run(() => _fileService.OpenAsync(
                "", OpenMode.Open, AccessIntent.Read, ShareIntent.Read, user));
            return Ok(new KaimoFileHandle(_fileService, root.Session));
        }

        IFileSession session = SmbSync.Run(() => _fileService.OpenSnapshotAsync(
            ShareRelativePath.Normalize(realPath), snapAt, user));
        return Ok(new KaimoFileHandle(_fileService, session));
    }

    private FileStoreResult<IFileHandle> CreateDirectory(
        string relative, CreateDispositionIntent disposition, UserContext user, out CreateOutcome createAction)
    {
        createAction = CreateOutcome.Opened;
        bool mustNotExist = disposition == CreateDispositionIntent.Create;
        bool canCreate = disposition is CreateDispositionIntent.Create
            or CreateDispositionIntent.OpenIf or CreateDispositionIntent.OverwriteIf or CreateDispositionIntent.Supersede;

        // Try opening an existing directory first (ACL-checked: ListReadData).
        bool exists = true;
        FileOpenResult? existing = null;
        try
        {
            existing = SmbSync.Run(() => _fileService.OpenAsync(
                relative, OpenMode.Open, AccessIntent.Read, ShareIntent.Read, user));
        }
        catch (FileNotFoundException) { exists = false; }
        catch (DirectoryNotFoundException) { exists = false; }

        if (exists && existing != null)
        {
            if (!existing.Session.IsDirectory)
            {
                SmbSync.Run(() => existing.Session.DisposeAsync());
                return Fail(NtStatus.ObjectNameCollision); // a file already occupies this name
            }
            if (mustNotExist)
            {
                SmbSync.Run(() => existing.Session.DisposeAsync());
                return Fail(NtStatus.ObjectNameCollision);
            }
            return Ok(new KaimoFileHandle(_fileService, existing.Session));
        }

        if (!canCreate)
            return Fail(NtStatus.ObjectNameNotFound);

        // Create (ACL-checked: CreateWriteData on the parent), then open a handle to it.
        SmbSync.Run(() => _fileService.CreateDirectoryAsync(relative, user));
        FileOpenResult opened = SmbSync.Run(() => _fileService.OpenAsync(
            relative, OpenMode.Open, AccessIntent.Read, ShareIntent.Read, user));
        createAction = CreateOutcome.Created;
        return Ok(new KaimoFileHandle(_fileService, opened.Session));
    }

    // ───────────────────────── READ / WRITE / FLUSH ─────────────────────────

    public FileStoreResult<int> Read(IFileHandle handle, long offset, Span<byte> buffer)
    {
        var h = (KaimoFileHandle)handle;
        if (h.IsDirectory) return FileStoreResult<int>.Fail(NtStatus.FileIsADirectory);
        try
        {
            var tmp = new byte[buffer.Length];
            int n = SmbSync.Run(() => h.Session.ReadAsync(offset, tmp, default));
            if (n > 0) tmp.AsSpan(0, n).CopyTo(buffer);
            return FileStoreResult<int>.Ok(n); // 0 → handler maps to STATUS_END_OF_FILE
        }
        catch (ObjectDisposedException) { return FileStoreResult<int>.Fail(NtStatus.FileClosed); }
        catch (Exception ex) { Console.WriteLine($"[ReadFile ERROR] {ex.Message}"); return FileStoreResult<int>.Fail(NtStatus.InvalidParameter); }
    }

    public FileStoreResult<int> Write(IFileHandle handle, long offset, ReadOnlySpan<byte> data)
    {
        var h = (KaimoFileHandle)handle;
        if (h.IsDirectory) return FileStoreResult<int>.Fail(NtStatus.FileIsADirectory);
        if (h.Session.IsReadOnly) return FileStoreResult<int>.Fail(NtStatus.AccessDenied);
        try
        {
            byte[] arr = data.ToArray();
            SmbSync.Run(() => h.Session.WriteAsync(offset, arr, default));
            return FileStoreResult<int>.Ok(arr.Length);
        }
        catch (UnauthorizedAccessException) { return FileStoreResult<int>.Fail(NtStatus.AccessDenied); }
        catch (ObjectDisposedException) { return FileStoreResult<int>.Fail(NtStatus.FileClosed); }
        catch (IOException) { return FileStoreResult<int>.Fail(NtStatus.DiskFull); }
        catch (Exception ex) { Console.WriteLine($"[WriteFile ERROR] {ex.Message}"); return FileStoreResult<int>.Fail(NtStatus.InvalidParameter); }
    }

    public NtStatus Flush(IFileHandle handle)
    {
        try { SmbSync.Run(() => ((KaimoFileHandle)handle).Session.FlushAsync(default)); return NtStatus.Success; }
        catch { return NtStatus.InvalidParameter; }
    }

    // ───────────────────────── QUERY DIRECTORY ─────────────────────────

    public FileStoreResult<IReadOnlyList<FileEntryInfo>> QueryDirectory(IFileHandle handle, string searchPattern)
    {
        var h = (KaimoFileHandle)handle;
        if (!h.IsDirectory) return FileStoreResult<IReadOnlyList<FileEntryInfo>>.Fail(NtStatus.InvalidParameter);

        try
        {
            List<FileMetadata> items = SmbSync.Run(() => _fileService.ListAsync(h.Path, h.Session.User));
            string pattern = string.IsNullOrEmpty(searchPattern) ? "*" : searchPattern;
            bool wildcard = pattern is "*" or "*.*";

            var result = new List<FileEntryInfo>(items.Count + 2);
            if (wildcard)
            {
                result.Add(KaimoFileInfo.Pseudo("."));
                result.Add(KaimoFileInfo.Pseudo(".."));
            }

            foreach (FileMetadata item in items)
            {
                if (!wildcard && !MatchesPattern(item.Name, pattern)) continue;
                result.Add(KaimoFileInfo.FromListing(item));
            }

            return FileStoreResult<IReadOnlyList<FileEntryInfo>>.Ok(result);
        }
        catch (UnauthorizedAccessException) { return FileStoreResult<IReadOnlyList<FileEntryInfo>>.Fail(NtStatus.AccessDenied); }
        catch (Exception ex)
        {
            Console.WriteLine($"[QueryDirectory ERROR] {ex.Message}");
            return FileStoreResult<IReadOnlyList<FileEntryInfo>>.Fail(NtStatus.InvalidParameter);
        }
    }

    // ───────────────────────── SET INFO ─────────────────────────

    public NtStatus SetEndOfFile(IFileHandle handle, long length)
    {
        var h = (KaimoFileHandle)handle;
        if (h.Session.IsReadOnly) return NtStatus.AccessDenied;
        try { SmbSync.Run(() => h.Session.SetLengthAsync(length, default)); return NtStatus.Success; }
        catch (UnauthorizedAccessException) { return NtStatus.AccessDenied; }
        catch { return NtStatus.InvalidParameter; }
    }

    public NtStatus Rename(IFileHandle handle, string newPath, bool replaceIfExists)
    {
        var h = (KaimoFileHandle)handle;
        try
        {
            // The session renames in place (close → move → reopen at storage level) so this handle
            // stays valid afterwards. ACL checks (Delete on source, Create on target) happen inside.
            string newRelative = ShareRelativePath.Normalize(newPath);
            SmbSync.Run(() => h.Session.FlushAsync(default));
            SmbSync.Run(() => h.Session.RenameAsync(newRelative, replaceIfExists, default));
            return NtStatus.Success;
        }
        catch (UnauthorizedAccessException) { return NtStatus.AccessDenied; }
        catch (IOException) { return NtStatus.ObjectNameCollision; }
        catch { return NtStatus.InvalidParameter; }
    }

    public NtStatus SetDeleteOnClose(IFileHandle handle, bool delete)
    {
        if (!delete) return NtStatus.Success; // clearing the flag — nothing to undo on the Kaimo side

        var h = (KaimoFileHandle)handle;
        try
        {
            // DELETE_ON_CLOSE requires the Delete permission — not implied by the open access.
            if (!SmbSync.Run(() => _fileService.CanDeleteAsync(h.Path, h.Session.User)))
                return NtStatus.AccessDenied;
            h.Session.MarkDeleteOnClose();
            return NtStatus.Success;
        }
        catch (UnauthorizedAccessException) { return NtStatus.AccessDenied; }
        catch { return NtStatus.AccessDenied; }
    }

    // ───────────────────────── SNAPSHOTS (ISnapshotStore) ─────────────────────────

    // Kaimo tracks snapshots per share (not per path); both queries return the share's timestamps,
    // matching the previous FSCTL_SRV_ENUMERATE_SNAPSHOTS behavior.
    public IReadOnlyList<DateTime> GetSnapshots(string path) => GetAllSnapshots();

    public IReadOnlyList<DateTime> GetAllSnapshots()
    {
        if (!TryGetUser(out UserContext user)) return Array.Empty<DateTime>();
        try { return SmbSync.Run(() => _fileService.GetSnapshotTimestampsAsync(user)); }
        catch (Exception ex) { Console.WriteLine($"[Snapshots ERROR] {ex.Message}"); return Array.Empty<DateTime>(); }
    }

    // ───────────────────────── VOLUME (IVolumeInfoProvider) ─────────────────────────

    public VolumeInfo GetVolumeInfo()
    {
        try
        {
            string root = _fileService.ToAbsolutePath("");
            var drive = new DriveInfo(System.IO.Path.GetPathRoot(root) ?? root);
            return new VolumeInfo("KaimoSMB", 0x12345678, drive.TotalSize, drive.AvailableFreeSpace);
        }
        catch
        {
            return new VolumeInfo("KaimoSMB", 0x12345678, -1, -1);
        }
    }

    // ───────────────────────── HELPERS ─────────────────────────

    private const ShareIntent AllShare = ShareIntent.Read | ShareIntent.Write | ShareIntent.Delete;

    private static bool TryGetUser(out UserContext user)
    {
        user = null!;
        if (SmbCaller.Current is not { } caller) return false;
        UserContext? resolved = KaimoUserRegistry.Lookup(caller.User);
        if (resolved == null) return false;
        user = resolved;
        return true;
    }

    private static FileStoreResult<IFileHandle> Ok(IFileHandle h) => FileStoreResult<IFileHandle>.Ok(h);
    private static FileStoreResult<IFileHandle> Fail(NtStatus s) => FileStoreResult<IFileHandle>.Fail(s);

    private static OpenMode MapDisposition(CreateDispositionIntent d) => d switch
    {
        CreateDispositionIntent.Create => OpenMode.Create,
        CreateDispositionIntent.Open => OpenMode.Open,
        CreateDispositionIntent.OpenIf => OpenMode.OpenOrCreate,
        CreateDispositionIntent.Overwrite => OpenMode.Truncate,
        CreateDispositionIntent.OverwriteIf => OpenMode.CreateOrTruncate,
        CreateDispositionIntent.Supersede => OpenMode.Supersede,
        _ => OpenMode.OpenOrCreate,
    };

    private static AccessIntent MapAccess(FileAccessIntent a)
    {
        bool read = a.HasFlag(FileAccessIntent.Read);
        bool write = a.HasFlag(FileAccessIntent.Write);
        if (read && write) return AccessIntent.ReadWrite;
        if (write) return AccessIntent.Write;
        return AccessIntent.Read; // read or delete-only opens read the entry
    }

    private static CreateOutcome MapOutcome(FileOpenStatus s) => s switch
    {
        FileOpenStatus.Created => CreateOutcome.Created,
        FileOpenStatus.Overwritten => CreateOutcome.Overwritten,
        FileOpenStatus.Superseded => CreateOutcome.Superseded,
        _ => CreateOutcome.Opened,
    };

    private static bool MatchesPattern(string name, string pattern)
    {
        string regex = "^" + System.Text.RegularExpressions.Regex.Escape(pattern)
            .Replace("\\*", ".*").Replace("\\?", ".") + "$";
        return System.Text.RegularExpressions.Regex.IsMatch(
            name, regex, System.Text.RegularExpressions.RegexOptions.IgnoreCase);
    }
}

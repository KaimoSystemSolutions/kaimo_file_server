using Kaimo_File_Server.Core.Domain;
using Kaimo_File_Server.Core.Domain.Identity;
using Kaimo_File_Server.Core.Helpers;
using Kaimo_File_Server.Core.Logging;
using Kaimo_File_Server.Core.Services.File;
using Kaimo_File_Server.Core.Storage;
using Microsoft.Extensions.Logging;
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
    private readonly ILogger<KaimoFileStore> _logger;

    public KaimoFileStore(Guid shareId, IFileService fileService, ILogger<KaimoFileStore> logger)
    {
        _shareId = shareId;
        _fileService = fileService;
        _logger = logger;
    }

    // ───────────────────────── CREATE ─────────────────────────

    public async ValueTask<FileStoreResult<FileCreateResult>> CreateAsync(
        string path, FileAccessIntent access, CreateDispositionIntent disposition,
        bool directoryRequired, bool nonDirectoryRequired, CancellationToken cancellationToken)
    {
        if (!TryGetUser(out UserContext user))
            return FailCreate(NtStatus.AccessDenied);

        try
        {
            // Snapshot path (@GMT-…\real\path) → previous version, read-only.
            if (GmtToken.TrySplitSnapshotPath(path, out DateTime snapAt, out string realPath))
                return await OpenSnapshotAsync(realPath, snapAt, user);

            string relative = ShareRelativePath.Normalize(path);

            if (directoryRequired)
                return await OpenOrCreateDirectoryAsync(relative, disposition, user);

            FileOpenResult result = await _fileService.OpenAsync(
                relative, MapDisposition(disposition), MapAccess(access), AllShare, user);

            if (nonDirectoryRequired && result.Session.IsDirectory)
            {
                await result.Session.DisposeAsync();
                return FailCreate(NtStatus.FileIsADirectory);
            }

            return OkCreate(new KaimoFileHandle(_fileService, result.Session), MapOutcome(result.Status));
        }
        catch (UnauthorizedAccessException) { return FailCreate(NtStatus.AccessDenied); }
        catch (FileNotFoundException) { return FailCreate(NtStatus.ObjectNameNotFound); }
        catch (DirectoryNotFoundException) { return FailCreate(NtStatus.ObjectPathNotFound); }
        catch (IOException ex) when ((ex.HResult & 0xFFFF) == 32) { return FailCreate(NtStatus.SharingViolation); }
        catch (IOException) when (disposition == CreateDispositionIntent.Create) { return FailCreate(NtStatus.ObjectNameCollision); }
        catch (Exception ex)
        {
            _logger.LogError(LogEvents.SmbCreateFailed, ex, LogMessages.SmbCreateFailed, path);
            return FailCreate(NtStatus.AccessDenied);
        }
    }

    private async ValueTask<FileStoreResult<FileCreateResult>> OpenSnapshotAsync(
        string realPath, DateTime snapAt, UserContext user)
    {
        // Snapshot root (no real path) → open the live share root read-only as a directory.
        if (string.IsNullOrEmpty(realPath))
        {
            FileOpenResult root = await _fileService.OpenAsync(
                "", OpenMode.Open, AccessIntent.Read, ShareIntent.Read, user);
            return OkCreate(new KaimoFileHandle(_fileService, root.Session), CreateOutcome.Opened);
        }

        IFileSession session = await _fileService.OpenSnapshotAsync(
            ShareRelativePath.Normalize(realPath), snapAt, user);
        return OkCreate(new KaimoFileHandle(_fileService, session), CreateOutcome.Opened);
    }

    private async ValueTask<FileStoreResult<FileCreateResult>> OpenOrCreateDirectoryAsync(
        string relative, CreateDispositionIntent disposition, UserContext user)
    {
        bool mustNotExist = disposition == CreateDispositionIntent.Create;
        bool canCreate = disposition is CreateDispositionIntent.Create
            or CreateDispositionIntent.OpenIf or CreateDispositionIntent.OverwriteIf or CreateDispositionIntent.Supersede;

        // Try opening an existing directory first (ACL-checked: ListReadData).
        bool exists = true;
        FileOpenResult? existing = null;
        try
        {
            existing = await _fileService.OpenAsync(
                relative, OpenMode.Open, AccessIntent.Read, ShareIntent.Read, user);
        }
        catch (FileNotFoundException) { exists = false; }
        catch (DirectoryNotFoundException) { exists = false; }

        if (exists && existing != null)
        {
            if (!existing.Session.IsDirectory)
            {
                await existing.Session.DisposeAsync();
                return FailCreate(NtStatus.ObjectNameCollision); // a file already occupies this name
            }
            if (mustNotExist)
            {
                await existing.Session.DisposeAsync();
                return FailCreate(NtStatus.ObjectNameCollision);
            }
            return OkCreate(new KaimoFileHandle(_fileService, existing.Session), CreateOutcome.Opened);
        }

        if (!canCreate)
            return FailCreate(NtStatus.ObjectNameNotFound);

        // Create (ACL-checked: CreateWriteData on the parent), then open a handle to it.
        await _fileService.CreateDirectoryAsync(relative, user);
        FileOpenResult opened = await _fileService.OpenAsync(
            relative, OpenMode.Open, AccessIntent.Read, ShareIntent.Read, user);
        return OkCreate(new KaimoFileHandle(_fileService, opened.Session), CreateOutcome.Created);
    }

    // ───────────────────────── READ / WRITE / FLUSH ─────────────────────────

    public async ValueTask<FileStoreResult<int>> ReadAsync(
        IFileHandle handle, long offset, Memory<byte> buffer, CancellationToken cancellationToken)
    {
        var h = (KaimoFileHandle)handle;
        if (h.IsDirectory) return FileStoreResult<int>.Fail(NtStatus.FileIsADirectory);
        try
        {
            int n = await h.Session.ReadAsync(offset, buffer, cancellationToken);
            return FileStoreResult<int>.Ok(n); // 0 → handler maps to STATUS_END_OF_FILE
        }
        catch (ObjectDisposedException) { return FileStoreResult<int>.Fail(NtStatus.FileClosed); }
        catch (Exception ex) { _logger.LogError(LogEvents.SmbReadFailed, ex, LogMessages.SmbReadFailed); return FileStoreResult<int>.Fail(NtStatus.InvalidParameter); }
    }

    public async ValueTask<FileStoreResult<int>> WriteAsync(
        IFileHandle handle, long offset, ReadOnlyMemory<byte> data, CancellationToken cancellationToken)
    {
        var h = (KaimoFileHandle)handle;
        if (h.IsDirectory) return FileStoreResult<int>.Fail(NtStatus.FileIsADirectory);
        if (h.Session.IsReadOnly) return FileStoreResult<int>.Fail(NtStatus.AccessDenied);
        try
        {
            await h.Session.WriteAsync(offset, data, cancellationToken);
            return FileStoreResult<int>.Ok(data.Length);
        }
        catch (UnauthorizedAccessException) { return FileStoreResult<int>.Fail(NtStatus.AccessDenied); }
        catch (ObjectDisposedException) { return FileStoreResult<int>.Fail(NtStatus.FileClosed); }
        catch (IOException) { return FileStoreResult<int>.Fail(NtStatus.DiskFull); }
        catch (Exception ex) { _logger.LogError(LogEvents.SmbWriteFailed, ex, LogMessages.SmbWriteFailed); return FileStoreResult<int>.Fail(NtStatus.InvalidParameter); }
    }

    public async ValueTask<NtStatus> FlushAsync(IFileHandle handle, CancellationToken cancellationToken)
    {
        try { await ((KaimoFileHandle)handle).Session.FlushAsync(cancellationToken); return NtStatus.Success; }
        catch { return NtStatus.InvalidParameter; }
    }

    // ───────────────────────── QUERY DIRECTORY ─────────────────────────

    public async ValueTask<FileStoreResult<IReadOnlyList<FileEntryInfo>>> QueryDirectoryAsync(
        IFileHandle handle, string searchPattern, CancellationToken cancellationToken)
    {
        var h = (KaimoFileHandle)handle;
        if (!h.IsDirectory) return FileStoreResult<IReadOnlyList<FileEntryInfo>>.Fail(NtStatus.InvalidParameter);

        try
        {
            List<FileMetadata> items = await _fileService.ListAsync(h.Path, h.Session.User);
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
            _logger.LogError(LogEvents.SmbQueryDirectoryFailed, ex, LogMessages.SmbQueryDirectoryFailed);
            return FileStoreResult<IReadOnlyList<FileEntryInfo>>.Fail(NtStatus.InvalidParameter);
        }
    }

    // ───────────────────────── SET INFO ─────────────────────────

    public async ValueTask<NtStatus> SetEndOfFileAsync(IFileHandle handle, long length, CancellationToken cancellationToken)
    {
        var h = (KaimoFileHandle)handle;
        if (h.Session.IsReadOnly) return NtStatus.AccessDenied;
        try { await h.Session.SetLengthAsync(length, cancellationToken); return NtStatus.Success; }
        catch (UnauthorizedAccessException) { return NtStatus.AccessDenied; }
        catch { return NtStatus.InvalidParameter; }
    }

    public async ValueTask<NtStatus> RenameAsync(IFileHandle handle, string newPath, bool replaceIfExists, CancellationToken cancellationToken)
    {
        var h = (KaimoFileHandle)handle;
        try
        {
            // The session renames in place (close → move → reopen at storage level) so this handle
            // stays valid afterwards. ACL checks (Delete on source, Create on target) happen inside.
            string newRelative = ShareRelativePath.Normalize(newPath);
            await h.Session.FlushAsync(cancellationToken);
            await h.Session.RenameAsync(newRelative, replaceIfExists, cancellationToken);
            return NtStatus.Success;
        }
        catch (UnauthorizedAccessException) { return NtStatus.AccessDenied; }
        catch (IOException) { return NtStatus.ObjectNameCollision; }
        catch { return NtStatus.InvalidParameter; }
    }

    public async ValueTask<NtStatus> SetDeleteOnCloseAsync(IFileHandle handle, bool delete, CancellationToken cancellationToken)
    {
        if (!delete) return NtStatus.Success; // clearing the flag — nothing to undo on the Kaimo side

        var h = (KaimoFileHandle)handle;
        try
        {
            // DELETE_ON_CLOSE requires the Delete permission — not implied by the open access.
            if (!await _fileService.CanDeleteAsync(h.Path, h.Session.User))
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
        catch (Exception ex) { _logger.LogError(LogEvents.SmbSnapshotsFailed, ex, LogMessages.SmbSnapshotsFailed); return Array.Empty<DateTime>(); }
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

    private static FileStoreResult<FileCreateResult> OkCreate(IFileHandle handle, CreateOutcome action)
        => FileStoreResult<FileCreateResult>.Ok(new FileCreateResult(handle, action));
    private static FileStoreResult<FileCreateResult> FailCreate(NtStatus s)
        => FileStoreResult<FileCreateResult>.Fail(s);

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

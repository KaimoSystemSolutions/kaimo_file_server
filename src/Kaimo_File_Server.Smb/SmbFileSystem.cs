using Kaimo_File_Server.Core.Domain.Identity;
using Kaimo_File_Server.Core.Helpers;
using Kaimo_File_Server.Core.Services.File;
using Kaimo_File_Server.Core.Storage;
using SMBLibrary;
using System.Collections.Concurrent;
using FileAttributes = SMBLibrary.FileAttributes;

namespace Kaimo_File_Server.Smb;

public class SmbFileSystem : INTFileStore
{
    private readonly string _shareName;
    private readonly IFileService _fileService;

    public SmbFileSystem(string shareName, IFileService fileService)
    {
        _shareName = shareName;
        _fileService = fileService;
    }

    // ═══════════════════════ USER CACHE (only for ABE) ═══════════════════════
    //
    // User context flows through IFileSession for ALL operations after CreateFile.
    // The only place we still need a username→UserContext lookup is the ABE
    // share enumeration (no handle exists there). One cache, no AsyncLocal.

    private static readonly ConcurrentDictionary<string, UserContext> _userCache
        = new(StringComparer.OrdinalIgnoreCase);

    public static void RegisterUser(UserContext user)
    {
        if (user?.User?.Username != null)
            _userCache[user.User.Username] = user;
    }

    public static UserContext? LookupUser(string? username)
        => username != null && _userCache.TryGetValue(username, out var u) ? u : null;

    // ═══════════════════════ HANDLE ═══════════════════════

    private sealed class SmbHandle
    {
        public IFileSession Session { get; init; } = null!;
        public bool IsDirectory => Session.IsDirectory;
        public string RelativePath => Session.RelativePath;
    }

    // ═══════════════════════ SYNC BRIDGE ═══════════════════════
    //
    // INTFileStore is synchronous. Until SMBLibrary itself goes async,
    // everything bridges here. ONE place, not 20.

    private static T Sync<T>(Func<Task<T>> f) => Task.Run(f).GetAwaiter().GetResult();
    private static T Sync<T>(Func<ValueTask<T>> f) => Task.Run(async () => await f()).GetAwaiter().GetResult();
    private static void Sync(Func<Task> f) => Task.Run(f).GetAwaiter().GetResult();
    private static void Sync(Func<ValueTask> f) => Task.Run(async () => await f()).GetAwaiter().GetResult();

    // ═══════════════════════ PATH HELPERS ═══════════════════════

    private static string ToShareRelative(string smbPath)
        => ShareRelativePath.Normalize(smbPath);

    // ═══════════════════════ CREATE ═══════════════════════

    public NTStatus CreateFile(
        out object handle, out FileStatus fileStatus, string path,
        AccessMask desiredAccess, FileAttributes fileAttributes, ShareAccess shareAccess,
        CreateDisposition createDisposition, CreateOptions createOptions,
        SecurityContext securityContext)
    {
        handle = null!;
        fileStatus = FileStatus.FILE_DOES_NOT_EXIST;

        try
        {
            var user = LookupUser(securityContext?.UserName)
                ?? throw new UnauthorizedAccessException("No user context available.");

            // Snapshot path? Route through FileService.OpenSnapshotAsync.
            if (SmbSnapshotHandler.IsSnapshotPath(path))
                return CreateSnapshotHandle(out handle, out fileStatus, path, user);

            var relativePath = ToShareRelative(path);
            var mode = MapDisposition(createDisposition);
            var intent = MapIntent(desiredAccess, createOptions);
            var share = MapShare(shareAccess);
            var deleteOnClose = (createOptions & CreateOptions.FILE_DELETE_ON_CLOSE) != 0;
            var wantsDirectory = (createOptions & CreateOptions.FILE_DIRECTORY_FILE) != 0;

            // Directory creation/open goes through a dedicated path: the storage
            // engine decides file-vs-directory from what already exists on disk,
            // so a brand-new directory must be created explicitly (ACL-checked)
            // before we can open a handle to it.
            if (wantsDirectory)
                return CreateDirectoryHandle(out handle, out fileStatus,
                    relativePath, createDisposition, share, deleteOnClose, user);

            var result = Sync(() => _fileService.OpenAsync(relativePath, mode, intent, share, user));

            // Delete-on-close requires the Delete permission — it is NOT implied
            // by the read/write access used to open the handle.
            if (deleteOnClose)
            {
                if (!Sync(() => _fileService.CanDeleteAsync(relativePath, user)))
                {
                    Sync(() => result.Session.DisposeAsync());
                    return NTStatus.STATUS_ACCESS_DENIED;
                }
                result.Session.MarkDeleteOnClose();
            }

            fileStatus = result.Status switch
            {
                FileOpenStatus.Created => FileStatus.FILE_CREATED,
                FileOpenStatus.Overwritten => FileStatus.FILE_OVERWRITTEN,
                FileOpenStatus.Superseded => FileStatus.FILE_SUPERSEDED,
                _ => FileStatus.FILE_OPENED
            };

            handle = new SmbHandle { Session = result.Session };
            return NTStatus.STATUS_SUCCESS;
        }
        catch (UnauthorizedAccessException) { return NTStatus.STATUS_ACCESS_DENIED; }
        catch (FileNotFoundException) { return NTStatus.STATUS_OBJECT_NAME_NOT_FOUND; }
        catch (DirectoryNotFoundException) { return NTStatus.STATUS_OBJECT_PATH_NOT_FOUND; }
        catch (IOException ex) when (ex.HResult == unchecked((int)0x80070020))
        { return NTStatus.STATUS_SHARING_VIOLATION; }
        catch (IOException) when (createDisposition == CreateDisposition.FILE_CREATE)
        { return NTStatus.STATUS_OBJECT_NAME_COLLISION; }
        catch (Exception ex)
        {
            Console.WriteLine($"[CreateFile ERROR] {path}: {ex.Message}");
            return NTStatus.STATUS_ACCESS_DENIED;
        }
    }

    private NTStatus CreateSnapshotHandle(
        out object handle, out FileStatus fileStatus, string path, UserContext user)
    {
        handle = null!;
        fileStatus = FileStatus.FILE_DOES_NOT_EXIST;

        var info = SmbSnapshotHandler.ParseSnapshotPath(path);
        if (info == null) return NTStatus.STATUS_OBJECT_NAME_NOT_FOUND;

        // Snapshot root → treat as directory open on live root
        if (string.IsNullOrEmpty(info.RealPath))
        {
            try
            {
                var dirResult = Sync(() => _fileService.OpenAsync(
                    "", OpenMode.Open, AccessIntent.Read, ShareIntent.Read, user));
                handle = new SmbHandle { Session = dirResult.Session };
                fileStatus = FileStatus.FILE_OPENED;
                return NTStatus.STATUS_SUCCESS;
            }
            catch (UnauthorizedAccessException) { return NTStatus.STATUS_ACCESS_DENIED; }
        }

        try
        {
            var session = Sync(() => _fileService.OpenSnapshotAsync(
                info.RealPath, info.SnapshotTimestamp, user));
            handle = new SmbHandle { Session = session };
            fileStatus = FileStatus.FILE_OPENED;
            return NTStatus.STATUS_SUCCESS;
        }
        catch (UnauthorizedAccessException) { return NTStatus.STATUS_ACCESS_DENIED; }
        catch (FileNotFoundException) { return NTStatus.STATUS_OBJECT_NAME_NOT_FOUND; }
        catch (Exception ex)
        {
            Console.WriteLine($"[Snapshot ERROR] {path}: {ex.Message}");
            return NTStatus.STATUS_OBJECT_NAME_NOT_FOUND;
        }
    }

    private NTStatus CreateDirectoryHandle(
        out object handle, out FileStatus fileStatus,
        string relativePath, CreateDisposition disposition,
        ShareIntent share, bool deleteOnClose, UserContext user)
    {
        handle = null!;
        fileStatus = FileStatus.FILE_DOES_NOT_EXIST;

        bool mustNotExistIfPresent = disposition == CreateDisposition.FILE_CREATE;
        bool canCreate = disposition is CreateDisposition.FILE_CREATE
            or CreateDisposition.FILE_OPEN_IF
            or CreateDisposition.FILE_OVERWRITE_IF
            or CreateDisposition.FILE_SUPERSEDE;

        // Try opening an already-existing directory first (ACL-checked: ListReadData).
        bool exists = true;
        FileOpenResult? existing = null;
        try
        {
            existing = Sync(() => _fileService.OpenAsync(
                relativePath, OpenMode.Open, AccessIntent.Read, share, user));
        }
        catch (FileNotFoundException) { exists = false; }
        catch (DirectoryNotFoundException) { exists = false; }
        catch (UnauthorizedAccessException) { return NTStatus.STATUS_ACCESS_DENIED; }

        if (exists && existing != null)
        {
            if (!existing.Session.IsDirectory)
            {
                // A file already occupies this name — not a directory.
                Sync(() => existing.Session.DisposeAsync());
                return NTStatus.STATUS_OBJECT_NAME_COLLISION;
            }

            if (mustNotExistIfPresent)
            {
                Sync(() => existing.Session.DisposeAsync());
                return NTStatus.STATUS_OBJECT_NAME_COLLISION;
            }

            if (deleteOnClose)
            {
                if (!Sync(() => _fileService.CanDeleteAsync(relativePath, user)))
                {
                    Sync(() => existing.Session.DisposeAsync());
                    return NTStatus.STATUS_ACCESS_DENIED;
                }
                existing.Session.MarkDeleteOnClose();
            }

            handle = new SmbHandle { Session = existing.Session };
            fileStatus = FileStatus.FILE_OPENED;
            return NTStatus.STATUS_SUCCESS;
        }

        // Directory does not exist.
        if (!canCreate)
            return NTStatus.STATUS_OBJECT_NAME_NOT_FOUND;

        // Create it (ACL-checked: CreateWriteData on the parent).
        try
        {
            Sync(() => _fileService.CreateDirectoryAsync(relativePath, user));
        }
        catch (UnauthorizedAccessException) { return NTStatus.STATUS_ACCESS_DENIED; }
        catch (IOException) { return NTStatus.STATUS_OBJECT_NAME_COLLISION; }

        var opened = Sync(() => _fileService.OpenAsync(
            relativePath, OpenMode.Open, AccessIntent.Read, share, user));

        if (deleteOnClose)
        {
            if (!Sync(() => _fileService.CanDeleteAsync(relativePath, user)))
            {
                Sync(() => opened.Session.DisposeAsync());
                return NTStatus.STATUS_ACCESS_DENIED;
            }
            opened.Session.MarkDeleteOnClose();
        }

        handle = new SmbHandle { Session = opened.Session };
        fileStatus = FileStatus.FILE_CREATED;
        return NTStatus.STATUS_SUCCESS;
    }

    // ═══════════════════════ READ / WRITE / FLUSH / CLOSE ═══════════════════════

    public NTStatus ReadFile(out byte[] data, object handle, long offset, int maxCount)
    {
        data = null!;
        if (handle is not SmbHandle h || h.IsDirectory) return NTStatus.STATUS_INVALID_HANDLE;

        try
        {
            var buffer = new byte[maxCount];
            var read = Sync(() => h.Session.ReadAsync(offset, buffer, default));

            if (read == 0) { data = Array.Empty<byte>(); return NTStatus.STATUS_END_OF_FILE; }
            if (read < maxCount) Array.Resize(ref buffer, read);
            data = buffer;
            return NTStatus.STATUS_SUCCESS;
        }
        catch (ObjectDisposedException) { return NTStatus.STATUS_FILE_CLOSED; }
        catch { return NTStatus.STATUS_DATA_ERROR; }
    }

    public NTStatus WriteFile(out int numberOfBytesWritten, object handle, long offset, byte[] data)
    {
        numberOfBytesWritten = 0;
        if (handle is not SmbHandle h || h.IsDirectory) return NTStatus.STATUS_INVALID_HANDLE;
        if (h.Session.IsReadOnly) return NTStatus.STATUS_ACCESS_DENIED;

        try
        {
            Sync(() => h.Session.WriteAsync(offset, data, default));
            numberOfBytesWritten = data.Length;
            return NTStatus.STATUS_SUCCESS;
        }
        catch (UnauthorizedAccessException) { return NTStatus.STATUS_ACCESS_DENIED; }
        catch (ObjectDisposedException) { return NTStatus.STATUS_FILE_CLOSED; }
        catch { return NTStatus.STATUS_DATA_ERROR; }
    }

    public NTStatus FlushFileBuffers(object handle)
    {
        if (handle is not SmbHandle h) return NTStatus.STATUS_INVALID_HANDLE;
        try { Sync(() => h.Session.FlushAsync(default)); return NTStatus.STATUS_SUCCESS; }
        catch { return NTStatus.STATUS_DATA_ERROR; }
    }

    public NTStatus CloseFile(object handle)
    {
        if (handle is not SmbHandle h) return NTStatus.STATUS_INVALID_HANDLE;
        try
        {
            Sync(() => h.Session.DisposeAsync());
            return NTStatus.STATUS_SUCCESS;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[CloseFile ERROR] {ex.Message}");
            return NTStatus.STATUS_ACCESS_DENIED;
        }
    }

    // ═══════════════════════ QUERY DIRECTORY ═══════════════════════

    public NTStatus QueryDirectory(
        out List<QueryDirectoryFileInformation> result,
        object handle, string fileName, FileInformationClass informationClass)
    {
        result = new();
        if (handle is not SmbHandle h || !h.IsDirectory) return NTStatus.STATUS_INVALID_HANDLE;

        try
        {
            var items = Sync(() => _fileService.ListAsync(h.RelativePath, h.Session.User));
            var pattern = string.IsNullOrEmpty(fileName) ? "*" : fileName;
            bool isWildcard = pattern is "*" or "*.*";

            if (isWildcard)
            {
                result.Add(BuildEntry(".", h.RelativePath, isDirectory: true, informationClass));
                result.Add(BuildEntry("..", ShareRelativePath.GetParent(h.RelativePath),
                                      isDirectory: true, informationClass));
            }

            foreach (var item in items)
            {
                if (!MatchesPattern(item.Name, pattern)) continue;
                result.Add(BuildEntryFromMetadata(item, informationClass));
            }

            return result.Count == 0 ? NTStatus.STATUS_NO_SUCH_FILE : NTStatus.STATUS_SUCCESS;
        }
        catch (UnauthorizedAccessException) { return NTStatus.STATUS_ACCESS_DENIED; }
        catch (Exception ex)
        {
            Console.WriteLine($"[QueryDirectory ERROR] {ex.Message}");
            return NTStatus.STATUS_DATA_ERROR;
        }
    }

    // ═══════════════════════ SET FILE INFO ═══════════════════════

    public NTStatus SetFileInformation(object handle, FileInformation information)
    {
        if (handle is not SmbHandle h) return NTStatus.STATUS_INVALID_HANDLE;

        try
        {
            switch (information)
            {
                case FileDispositionInformation disp:
                    if (disp.DeletePending)
                    {
                        // Marking a handle for deletion requires the Delete
                        // permission — independent of how the handle was opened.
                        if (!Sync(() => _fileService.CanDeleteAsync(h.RelativePath, h.Session.User)))
                            return NTStatus.STATUS_ACCESS_DENIED;
                        h.Session.MarkDeleteOnClose();
                    }
                    return NTStatus.STATUS_SUCCESS;

                case FileRenameInformationType2 rename:
                    return HandleRename(h, rename);

                case FileBasicInformation basic:
                    Sync(() => h.Session.SetTimesAsync(new FileTimes(
                        basic.CreationTime.Time, basic.LastWriteTime.Time, basic.LastAccessTime.Time),
                        default));
                    return NTStatus.STATUS_SUCCESS;

                case FileEndOfFileInformation eof:
                    Sync(() => h.Session.SetLengthAsync(eof.EndOfFile, default));
                    return NTStatus.STATUS_SUCCESS;

                case FileAllocationInformation alloc when h.Session.Length > alloc.AllocationSize:
                    Sync(() => h.Session.SetLengthAsync(alloc.AllocationSize, default));
                    return NTStatus.STATUS_SUCCESS;

                case FileAllocationInformation:
                    return NTStatus.STATUS_SUCCESS;

                default:
                    return NTStatus.STATUS_NOT_SUPPORTED;
            }
        }
        catch (UnauthorizedAccessException) { return NTStatus.STATUS_ACCESS_DENIED; }
        catch (IOException) { return NTStatus.STATUS_SHARING_VIOLATION; }
        catch (Exception ex)
        {
            Console.WriteLine($"[SetFileInformation ERROR] {ex.Message}");
            return NTStatus.STATUS_DATA_ERROR;
        }
    }

    private NTStatus HandleRename(SmbHandle h, FileRenameInformationType2 rename)
    {
        var newRelative = ToShareRelative(rename.FileName);
        try
        {
            // The session renames in place (close → move → reopen at storage level),
            // so this SMB handle stays valid for any follow-up requests. ACL checks
            // (Delete on source, Create on target) happen inside RenameAsync.
            Sync(() => h.Session.FlushAsync(default));
            Sync(() => h.Session.RenameAsync(newRelative, rename.ReplaceIfExists, default));
            return NTStatus.STATUS_SUCCESS;
        }
        catch (UnauthorizedAccessException) { return NTStatus.STATUS_ACCESS_DENIED; }
        catch (IOException) { return NTStatus.STATUS_OBJECT_NAME_COLLISION; }
    }

    // ═══════════════════════ GET FILE INFO ═══════════════════════

    public NTStatus GetFileInformation(
        out FileInformation result, object handle, FileInformationClass informationClass)
    {
        result = null!;
        if (handle is not SmbHandle h) return NTStatus.STATUS_INVALID_HANDLE;

        try
        {
            var meta = Sync(() => _fileService.GetMetadataAsync(h.RelativePath, h.Session.User));

            // The open handle is authoritative for the current size — on-disk
            // metadata can lag behind an in-progress write until it is flushed.
            if (!h.IsDirectory) meta.Size = h.Session.Length;

            result = BuildFileInformation(meta, informationClass, h.Session.IsReadOnly);
            return NTStatus.STATUS_SUCCESS;
        }
        catch (UnauthorizedAccessException) { return NTStatus.STATUS_ACCESS_DENIED; }
        catch (Exception ex)
        {
            Console.WriteLine($"[GetFileInformation ERROR] {ex.Message}");
            return NTStatus.STATUS_DATA_ERROR;
        }
    }

    // ═══════════════════════ IOCTL (SNAPSHOTS) ═══════════════════════

    public NTStatus DeviceIOControl(object handle, uint ctlCode, byte[] input, out byte[] output, int maxOutputLength)
    {
        output = null!;
        if (ctlCode != SmbSnapshotHandler.FSCTL_SRV_ENUMERATE_SNAPSHOTS)
            return NTStatus.STATUS_NOT_SUPPORTED;

        if (handle is not SmbHandle h)
            return NTStatus.STATUS_INVALID_HANDLE;

        try
        {
            var timestamps = Sync(() => _fileService.GetSnapshotTimestampsAsync(h.Session.User));
            output = SmbSnapshotHandler.BuildEnumerateSnapshotsResponse(timestamps);
            if (output.Length > maxOutputLength) Array.Resize(ref output, maxOutputLength);
            return NTStatus.STATUS_SUCCESS;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[IOCTL ENUMERATE_SNAPSHOTS ERROR] {ex.Message}");
            return NTStatus.STATUS_NOT_SUPPORTED;
        }
    }

    /// <summary>Set by SmbServer at construction so we don't pull a ServiceProvider through.</summary>
    public Func<List<DateTime>>? SnapshotProvider { get; set; }

    // ═══════════════════════ STUBS ═══════════════════════

    public NTStatus Cancel(object ioRequest) => NTStatus.STATUS_SUCCESS;
    public NTStatus LockFile(object h, long o, long l, bool excl) => NTStatus.STATUS_SUCCESS;
    public NTStatus UnlockFile(object h, long o, long l) => NTStatus.STATUS_SUCCESS;
    public NTStatus NotifyChange(out object req, object h, NotifyChangeFilter f, bool tree,
        int sz, OnNotifyChangeCompleted cb, object ctx)
    { req = null!; return NTStatus.STATUS_NOT_SUPPORTED; }
    public NTStatus SetFileSystemInformation(FileSystemInformation info) => NTStatus.STATUS_NOT_SUPPORTED;

    public NTStatus GetSecurityInformation(out SecurityDescriptor d, object h, SecurityInformation i)
    { d = new SecurityDescriptor(); return NTStatus.STATUS_SUCCESS; }
    public NTStatus SetSecurityInformation(object h, SecurityInformation i, SecurityDescriptor d)
        => NTStatus.STATUS_SUCCESS;

    public NTStatus GetFileSystemInformation(out FileSystemInformation r, FileSystemInformationClass cls)
    {
        r = null!;
        try
        {
            var shareRoot = _fileService.ToAbsolutePath("");
            var driveInfo = new DriveInfo(Path.GetPathRoot(shareRoot) ?? shareRoot);

            r = cls switch
            {
                FileSystemInformationClass.FileFsVolumeInformation =>
                    new FileFsVolumeInformation { VolumeLabel = "KaimoSMB", VolumeSerialNumber = 0x12345678 },

                FileSystemInformationClass.FileFsSizeInformation =>
                    new FileFsSizeInformation
                    {
                        TotalAllocationUnits = driveInfo.TotalSize / 4096,
                        AvailableAllocationUnits = driveInfo.AvailableFreeSpace / 4096,
                        SectorsPerAllocationUnit = 8,
                        BytesPerSector = 512
                    },

                FileSystemInformationClass.FileFsFullSizeInformation =>
                    new FileFsFullSizeInformation
                    {
                        TotalAllocationUnits = driveInfo.TotalSize / 4096,
                        CallerAvailableAllocationUnits = driveInfo.AvailableFreeSpace / 4096,
                        ActualAvailableAllocationUnits = driveInfo.AvailableFreeSpace / 4096,
                        SectorsPerAllocationUnit = 8,
                        BytesPerSector = 512
                    },

                FileSystemInformationClass.FileFsDeviceInformation =>
                    new FileFsDeviceInformation { DeviceType = DeviceType.Disk, Characteristics = 0 },

                FileSystemInformationClass.FileFsAttributeInformation =>
                    new FileFsAttributeInformation
                    {
                        FileSystemAttributes = FileSystemAttributes.UnicodeOnDisk
                                             | FileSystemAttributes.CasePreservedNames,
                        MaximumComponentNameLength = 255,
                        FileSystemName = "NTFS"
                    },

                _ => null!
            };

            return r != null ? NTStatus.STATUS_SUCCESS : NTStatus.STATUS_INVALID_PARAMETER;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[GetFileSystemInformation ERROR] {ex.Message}");
            r = new FileFsVolumeInformation { VolumeLabel = "KaimoSMB" };
            return NTStatus.STATUS_SUCCESS;
        }
    }

    // ═══════════════════════ MAPPING / BUILDERS ═══════════════════════

    private static OpenMode MapDisposition(CreateDisposition d) => d switch
    {
        CreateDisposition.FILE_CREATE => OpenMode.Create,
        CreateDisposition.FILE_OPEN => OpenMode.Open,
        CreateDisposition.FILE_OPEN_IF => OpenMode.OpenOrCreate,
        CreateDisposition.FILE_OVERWRITE => OpenMode.Truncate,
        CreateDisposition.FILE_OVERWRITE_IF => OpenMode.CreateOrTruncate,
        CreateDisposition.FILE_SUPERSEDE => OpenMode.Supersede,
        _ => OpenMode.OpenOrCreate
    };

    private static AccessIntent MapIntent(AccessMask access, CreateOptions opts)
    {
        bool read = (access & (AccessMask.GENERIC_READ | AccessMask.GENERIC_ALL
                    | (AccessMask)FileAccessMask.FILE_READ_DATA)) != 0;
        bool write = (access & (AccessMask.GENERIC_WRITE | AccessMask.GENERIC_ALL
                    | (AccessMask)FileAccessMask.FILE_WRITE_DATA
                    | (AccessMask)FileAccessMask.FILE_APPEND_DATA)) != 0;
        if (read && write) return AccessIntent.ReadWrite;
        if (write) return AccessIntent.Write;
        return AccessIntent.Read;
    }

    private static ShareIntent MapShare(ShareAccess s)
    {
        var r = ShareIntent.None;
        if ((s & ShareAccess.Read) != 0) r |= ShareIntent.Read;
        if ((s & ShareAccess.Write) != 0) r |= ShareIntent.Write;
        if ((s & ShareAccess.Delete) != 0) r |= ShareIntent.Delete;
        return r;
    }

    private static bool MatchesPattern(string name, string pattern)
    {
        if (pattern is "*" or "*.*") return true;
        var regex = "^" + System.Text.RegularExpressions.Regex.Escape(pattern)
            .Replace("\\*", ".*").Replace("\\?", ".") + "$";
        return System.Text.RegularExpressions.Regex.IsMatch(
            name, regex, System.Text.RegularExpressions.RegexOptions.IgnoreCase);
    }

    // ───── Directory-entry builders (QueryDirectory) ─────

    /// <summary>
    /// Synthetic entry used only for the "." and ".." pseudo-directories.
    /// We don't have FileMetadata for these, so we emit a plain directory
    /// entry with the current time — Windows ignores their timestamps.
    /// </summary>
    private static QueryDirectoryFileInformation BuildEntry(
        string name, string relativePath, bool isDirectory, FileInformationClass cls)
    {
        var now = DateTime.UtcNow;
        var attrs = isDirectory ? FileAttributes.Directory : FileAttributes.Normal;
        return BuildDirEntry(name, name, attrs, now, now, now, 0, 0, cls);
    }

    private static QueryDirectoryFileInformation BuildEntryFromMetadata(
        Core.Domain.FileMetadata meta, FileInformationClass cls)
    {
        var attrs = meta.IsDirectory ? FileAttributes.Directory : FileAttributes.Normal;
        long size = meta.IsDirectory ? 0 : meta.Size;
        long alloc = RoundUpAllocation(size);
        var created = meta.CreatedAt;
        var modified = meta.ModifiedAt;
        var accessed = meta.LastAccessedAt ?? meta.ModifiedAt;
        var shortName = GenerateShortName(meta.Name);

        return BuildDirEntry(meta.Name, shortName, attrs, created, modified, accessed,
            size, alloc, cls);
    }

    private static QueryDirectoryFileInformation BuildDirEntry(
        string name, string shortName, FileAttributes attrs,
        DateTime created, DateTime modified, DateTime accessed,
        long endOfFile, long allocation, FileInformationClass cls) => cls switch
    {
        FileInformationClass.FileDirectoryInformation => new FileDirectoryInformation
        {
            FileName = name,
            CreationTime = created,
            LastAccessTime = accessed,
            LastWriteTime = modified,
            ChangeTime = modified,
            EndOfFile = endOfFile,
            AllocationSize = allocation,
            FileAttributes = attrs
        },
        FileInformationClass.FileBothDirectoryInformation => new FileBothDirectoryInformation
        {
            FileName = name,
            ShortName = shortName,
            CreationTime = created,
            LastAccessTime = accessed,
            LastWriteTime = modified,
            ChangeTime = modified,
            EndOfFile = endOfFile,
            AllocationSize = allocation,
            FileAttributes = attrs,
            EaSize = 0
        },
        FileInformationClass.FileIdBothDirectoryInformation => new FileIdBothDirectoryInformation
        {
            FileName = name,
            ShortName = shortName,
            CreationTime = created,
            LastAccessTime = accessed,
            LastWriteTime = modified,
            ChangeTime = modified,
            EndOfFile = endOfFile,
            AllocationSize = allocation,
            FileAttributes = attrs,
            EaSize = 0,
            FileId = 0
        },
        FileInformationClass.FileNamesInformation => new FileNamesInformation { FileName = name },
        _ => new FileDirectoryInformation
        {
            FileName = name,
            CreationTime = created,
            LastAccessTime = accessed,
            LastWriteTime = modified,
            ChangeTime = modified,
            EndOfFile = endOfFile,
            AllocationSize = allocation,
            FileAttributes = attrs
        }
    };

    // ───── Single-handle info builder (GetFileInformation) ─────

    private static FileInformation BuildFileInformation(
        Core.Domain.FileMetadata meta, FileInformationClass cls, bool readOnly)
    {
        var attrs = meta.IsDirectory ? FileAttributes.Directory : FileAttributes.Normal;
        long size = meta.IsDirectory ? 0 : meta.Size;
        long alloc = RoundUpAllocation(size);
        var created = meta.CreatedAt;
        var modified = meta.ModifiedAt;
        var accessed = meta.LastAccessedAt ?? meta.ModifiedAt;

        return cls switch
        {
            FileInformationClass.FileBasicInformation => new FileBasicInformation
            {
                CreationTime = created,
                LastWriteTime = modified,
                LastAccessTime = accessed,
                ChangeTime = modified,
                FileAttributes = attrs
            },
            FileInformationClass.FileStandardInformation => new FileStandardInformation
            {
                AllocationSize = alloc,
                EndOfFile = size,
                NumberOfLinks = 1,
                DeletePending = false,
                Directory = meta.IsDirectory
            },
            FileInformationClass.FileNetworkOpenInformation => new FileNetworkOpenInformation
            {
                CreationTime = created,
                LastWriteTime = modified,
                LastAccessTime = accessed,
                ChangeTime = modified,
                AllocationSize = alloc,
                EndOfFile = size,
                FileAttributes = attrs
            },
            FileInformationClass.FileInternalInformation =>
                new FileInternalInformation { IndexNumber = 0 },
            FileInformationClass.FileEaInformation =>
                new FileEaInformation { EaSize = 0 },
            FileInformationClass.FileAttributeTagInformation =>
                new FileAttributeTagInformation { FileAttributes = attrs, ReparsePointTag = 0 },
            FileInformationClass.FileStreamInformation when !meta.IsDirectory =>
                CreateFileStreamInfo(size, alloc),
            _ => new FileBasicInformation
            {
                CreationTime = created,
                LastWriteTime = modified,
                LastAccessTime = accessed,
                ChangeTime = modified,
                FileAttributes = attrs
            }
        };
    }

    // ───── Builder helpers ─────

    private static long RoundUpAllocation(long size)
    {
        const long clusterSize = 4096;
        return size == 0 ? 0 : ((size + clusterSize - 1) / clusterSize) * clusterSize;
    }

    private static string GenerateShortName(string name)
    {
        if (name.Length <= 12) return name;
        string ext = Path.GetExtension(name);
        string baseName = Path.GetFileNameWithoutExtension(name);
        if (ext.Length > 4) ext = ext[..4];
        int baseLen = Math.Min(baseName.Length, 6);
        return baseName[..baseLen].ToUpperInvariant() + "~1" + ext.ToUpperInvariant();
    }

    private static FileStreamInformation CreateFileStreamInfo(long fileSize, long allocSize)
    {
        var info = new FileStreamInformation();
        info.Entries.Add(new FileStreamEntry
        {
            StreamName = "::$DATA",
            StreamSize = fileSize,
            StreamAllocationSize = allocSize
        });
        return info;
    }
}
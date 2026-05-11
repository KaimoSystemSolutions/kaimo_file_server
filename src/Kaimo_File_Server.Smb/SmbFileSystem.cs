using Kaimo_File_Server.Core.Domain;
using Kaimo_File_Server.Core.Services;
using Microsoft.Extensions.DependencyInjection;
using SMBLibrary;
using SMBLibrary.Server;
using FileAttributes = SMBLibrary.FileAttributes;

namespace Kaimo_File_Server.Smb
{
    public class SmbFileSystem : INTFileStore
    {
        private readonly string _root;
        private readonly IFileService _fileService;
        private readonly IFileVersionService? _versionService;
        private readonly IServiceProvider? _serviceProvider;

        public SmbFileSystem(string rootPath, IFileService fileService,
            IServiceProvider? serviceProvider = null)
        {
            _root = rootPath;
            _fileService = fileService;
            _serviceProvider = serviceProvider;
            Directory.CreateDirectory(_root);
        }

        // ── Thread-safe user injection ──
        // Instead of a mutable CurrentUser property (race condition!),
        // each session stores its UserContext in AsyncLocal, set by OnAccessRequested.
        // The FileHandle then captures the user at creation time.
        private static readonly AsyncLocal<UserContext?> _sessionUser = new();

        /// <summary>
        /// Sets the UserContext for the current async/thread flow.
        /// Called by SmbServer.OnAccessRequested per-session.
        /// Thread-safe: AsyncLocal is isolated per execution context.
        /// </summary>
        public static void SetSessionUser(UserContext user)
        {
            _sessionUser.Value = user;
        }

        private string GetFullPath(string path)
        {
            // ── Path traversal protection ──
            path = path.Replace('\\', Path.DirectorySeparatorChar)
                       .TrimStart(Path.DirectorySeparatorChar);
            var root = Path.GetFullPath(_root)
                .TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            var full = Path.GetFullPath(Path.Combine(root, path));
            if (!full.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                throw new UnauthorizedAccessException("Path traversal detected");
            return full;
        }

        /// <summary>
        /// Returns the storage-relative path for permission checks via IFileService.
        /// E.g. fullPath="/data/storage/test/docs/file.txt", _root="/data/storage/test"
        ///   => "docs/file.txt"
        /// </summary>
        private string GetRelativePath(string fullPath)
        {
            var root = Path.GetFullPath(_root)
                .TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            var full = Path.GetFullPath(fullPath);
            if (!full.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                return fullPath;
            return full.Substring(root.Length)
                .Replace('\\', '/');  // ★ Immer forward slashes für Konsistenz
        }

        private sealed class FileHandle
        {
            public FileStream? Stream;
            public string Path = string.Empty;
            public bool IsDirectory;
            public bool DeleteOnClose;
            public UserContext User { get; init; } = null!;

            /// <summary>
            /// Set to true on the first WriteFile call.
            /// Used by CloseFile to decide whether versioning is needed.
            /// If false, the file was only read → no new version.
            /// </summary>
            public bool WasDirty { get; set; }
        }

        private UserContext RequireSessionUser()
        {
            return _sessionUser.Value
                ?? throw new InvalidOperationException(
                    "No authenticated user context on this execution flow. " +
                    "SetSessionUser must be called before filesystem operations.");
        }

        // ===================== CREATE =====================
        public NTStatus CreateFile(
            out object handle, out FileStatus fileStatus, string path,
            AccessMask desiredAccess, FileAttributes fileAttributes, ShareAccess shareAccess,
            CreateDisposition createDisposition, CreateOptions createOptions,
            SecurityContext securityContext)
        {
            Console.WriteLine($"[CreateFile] path='{path}' disposition={createDisposition} options={createOptions}");

            handle = null!;
            fileStatus = FileStatus.FILE_DOES_NOT_EXIST;

            try
            {
                var user = RequireSessionUser();

                // ── Snapshot path? Route to version store (readonly) ──
                if (SmbSnapshotHandler.IsSnapshotPath(path) && _versionService != null)
                {
                    Console.WriteLine($"[SNAPSHOT DEBUG] Incoming path: '{path}'");
                    var debugInfo = SmbSnapshotHandler.ParseSnapshotPath(path);
                    Console.WriteLine($"[SNAPSHOT DEBUG] Parsed: ts={debugInfo?.SnapshotTimestamp:O}, real='{debugInfo?.RealPath}'");
                    return OpenSnapshotFile(out handle, out fileStatus, path, user);
                }

                string fullPath = GetFullPath(path);
                string relativePath = GetRelativePath(fullPath);
                bool isDirectory = (createOptions & CreateOptions.FILE_DIRECTORY_FILE) != 0;
                if (!isDirectory && Directory.Exists(fullPath)) isDirectory = true;

                if (isDirectory)
                    return CreateDirectory(out handle, out fileStatus, fullPath, relativePath,
                        createDisposition, createOptions, user);

                return CreateRegularFile(out handle, out fileStatus, fullPath, relativePath,
                    createDisposition, createOptions, desiredAccess, shareAccess, user);
            }
            catch (InvalidOperationException) { throw; } // no user let it bubble
            catch (UnauthorizedAccessException) { return NTStatus.STATUS_ACCESS_DENIED; }
            catch (IOException ex) when (ex.HResult == unchecked((int)0x80070020))
            { return NTStatus.STATUS_SHARING_VIOLATION; }
            catch (Exception ex)
            {
                Console.WriteLine($"[CreateFile ERROR] {path}: {ex.Message}");
                return NTStatus.STATUS_ACCESS_DENIED;
            }
        }

        private NTStatus CreateDirectory(
            out object handle, out FileStatus fileStatus,
            string fullPath, string relativePath,
            CreateDisposition createDisposition, CreateOptions createOptions,
            UserContext user)
        {
            handle = null!;
            fileStatus = FileStatus.FILE_DOES_NOT_EXIST;

            if (createDisposition == CreateDisposition.FILE_CREATE)
            {
                if (Directory.Exists(fullPath))
                { fileStatus = FileStatus.FILE_EXISTS; return NTStatus.STATUS_OBJECT_NAME_COLLISION; }

                if (!CanCreate(relativePath, user))
                    return NTStatus.STATUS_ACCESS_DENIED;

                Directory.CreateDirectory(fullPath);
                fileStatus = FileStatus.FILE_CREATED;
            }
            else if (createDisposition == CreateDisposition.FILE_OPEN)
            {
                if (!Directory.Exists(fullPath))
                { fileStatus = FileStatus.FILE_DOES_NOT_EXIST; return NTStatus.STATUS_OBJECT_PATH_NOT_FOUND; }
                fileStatus = FileStatus.FILE_OPENED;
            }
            else
            {
                if (!Directory.Exists(fullPath))
                {
                    if (!CanCreate(relativePath, user))
                        return NTStatus.STATUS_ACCESS_DENIED;
                    Directory.CreateDirectory(fullPath);
                    fileStatus = FileStatus.FILE_CREATED;
                }
                else
                    fileStatus = FileStatus.FILE_OPENED;
            }

            handle = new FileHandle
            {
                Path = fullPath,
                IsDirectory = true,
                User = user,
                DeleteOnClose = (createOptions & CreateOptions.FILE_DELETE_ON_CLOSE) != 0
            };
            return NTStatus.STATUS_SUCCESS;
        }

        private NTStatus CreateRegularFile(
            out object handle, out FileStatus fileStatus,
            string fullPath, string relativePath,
            CreateDisposition createDisposition, CreateOptions createOptions,
            AccessMask desiredAccess, ShareAccess shareAccess, UserContext user)
        {
            handle = null!;
            fileStatus = FileStatus.FILE_DOES_NOT_EXIST;

            string? parentDir = Path.GetDirectoryName(fullPath);
            if (parentDir != null && !Directory.Exists(parentDir))
                return NTStatus.STATUS_OBJECT_PATH_NOT_FOUND;

            bool exists = File.Exists(fullPath);
            switch (createDisposition)
            {
                case CreateDisposition.FILE_OPEN:
                case CreateDisposition.FILE_OVERWRITE:
                    if (!exists) return NTStatus.STATUS_OBJECT_NAME_NOT_FOUND;
                    break;
                case CreateDisposition.FILE_CREATE:
                    if (exists)
                    { fileStatus = FileStatus.FILE_EXISTS; return NTStatus.STATUS_OBJECT_NAME_COLLISION; }
                    break;
            }

            // ── Single permission check ──
            // Write-creating operations check CanWrite; read-only opens check CanRead.
            // This is the ONLY permission gate FileService is not called again during
            // ReadFile/WriteFile to avoid double-checking.
            bool needsWrite = createDisposition != CreateDisposition.FILE_OPEN;
            if (needsWrite)
            {
                if (!CanWrite(relativePath, user))
                    return NTStatus.STATUS_ACCESS_DENIED;
            }
            else
            {
                if (!CanRead(relativePath, user))
                    return NTStatus.STATUS_ACCESS_DENIED;
            }

            FileMode mode = createDisposition switch
            {
                CreateDisposition.FILE_CREATE => FileMode.CreateNew,
                CreateDisposition.FILE_OPEN => FileMode.Open,
                CreateDisposition.FILE_OPEN_IF => FileMode.OpenOrCreate,
                CreateDisposition.FILE_OVERWRITE => FileMode.Truncate,
                CreateDisposition.FILE_OVERWRITE_IF => FileMode.Create,
                CreateDisposition.FILE_SUPERSEDE => FileMode.Create,
                _ => FileMode.OpenOrCreate
            };

            FileAccess fileAccess = MapFileAccess(desiredAccess);
            FileShare fileShare = MapFileShare(shareAccess);
            var fs = new FileStream(fullPath, mode, fileAccess, fileShare);

            if (createDisposition == CreateDisposition.FILE_SUPERSEDE && exists)
                fileStatus = FileStatus.FILE_SUPERSEDED;
            else if ((createDisposition == CreateDisposition.FILE_OVERWRITE
                   || createDisposition == CreateDisposition.FILE_OVERWRITE_IF) && exists)
                fileStatus = FileStatus.FILE_OVERWRITTEN;
            else if (!exists)
                fileStatus = FileStatus.FILE_CREATED;
            else
                fileStatus = FileStatus.FILE_OPENED;

            handle = new FileHandle
            {
                Stream = fs,
                Path = fullPath,
                IsDirectory = false,
                User = user,
                DeleteOnClose = (createOptions & CreateOptions.FILE_DELETE_ON_CLOSE) != 0
            };
            return NTStatus.STATUS_SUCCESS;
        }

        // ── Permission helpers ──
        // Wrap the async IFileService calls. SMBLibrary forces sync interfaces,
        // so we use Task.Run to avoid blocking the calling sync context.
        private bool CanRead(string relativePath, UserContext user)
            => Task.Run(() => _fileService.CanReadAsync(relativePath, user)).GetAwaiter().GetResult();

        private bool CanWrite(string relativePath, UserContext user)
            => Task.Run(() => _fileService.CanWriteAsync(relativePath, user)).GetAwaiter().GetResult();

        private bool CanCreate(string relativePath, UserContext user)
            => Task.Run(() => _fileService.CanCreateAsync(relativePath, user)).GetAwaiter().GetResult();

        private bool CanDelete(string relativePath, UserContext user)
            => Task.Run(() => _fileService.CanDeleteAsync(relativePath, user)).GetAwaiter().GetResult();

        private static FileAccess MapFileAccess(AccessMask desiredAccess)
        {
            bool read = (desiredAccess & (AccessMask.GENERIC_READ | AccessMask.GENERIC_ALL
                | (AccessMask)FileAccessMask.FILE_READ_DATA
                | (AccessMask)FileAccessMask.FILE_READ_ATTRIBUTES
                | (AccessMask)FileAccessMask.FILE_READ_EA)) != 0;
            bool write = (desiredAccess & (AccessMask.GENERIC_WRITE | AccessMask.GENERIC_ALL
                | (AccessMask)FileAccessMask.FILE_WRITE_DATA
                | (AccessMask)FileAccessMask.FILE_APPEND_DATA
                | (AccessMask)FileAccessMask.FILE_WRITE_ATTRIBUTES
                | (AccessMask)FileAccessMask.FILE_WRITE_EA)) != 0;
            if (read && write) return FileAccess.ReadWrite;
            if (write) return FileAccess.ReadWrite;
            return FileAccess.Read;
        }

        private static FileShare MapFileShare(ShareAccess shareAccess)
        {
            FileShare result = FileShare.None;
            if ((shareAccess & ShareAccess.Read) != 0) result |= FileShare.Read;
            if ((shareAccess & ShareAccess.Write) != 0) result |= FileShare.Write;
            if ((shareAccess & ShareAccess.Delete) != 0) result |= FileShare.Delete;
            return result;
        }

        // ===================== READ / WRITE =====================
        // No additional permission checks here permissions were validated
        // when the handle was created in CreateFile. The handle's User is
        // captured immutably, so there's no TOCTOU issue within a session.
        public NTStatus ReadFile(out byte[] data, object handle, long offset, int maxCount)
        {
            data = null!;

            // ── Snapshot file: read from decompressed version blob ──
            if (handle is SnapshotFileHandle sfh)
            {
                try
                {
                    sfh.Stream.Position = offset;
                    byte[] buffer = new byte[maxCount];
                    int read = sfh.Stream.Read(buffer, 0, maxCount);
                    if (read == 0) { data = Array.Empty<byte>(); return NTStatus.STATUS_END_OF_FILE; }
                    if (read < maxCount) Array.Resize(ref buffer, read);
                    data = buffer;
                    return NTStatus.STATUS_SUCCESS;
                }
                catch { return NTStatus.STATUS_DATA_ERROR; }
            }

            // ── Normal file read (existing code follows unchanged) ──
            var h = handle as FileHandle;
            if (h == null || h.IsDirectory) return NTStatus.STATUS_INVALID_HANDLE;
            if (h.Stream == null) return NTStatus.STATUS_FILE_CLOSED;

            try
            {
                h.Stream.Position = offset;
                byte[] buffer = new byte[maxCount];
                int read = h.Stream.Read(buffer, 0, maxCount);
                if (read == 0) { data = Array.Empty<byte>(); return NTStatus.STATUS_END_OF_FILE; }
                if (read < maxCount) Array.Resize(ref buffer, read);
                data = buffer;
                return NTStatus.STATUS_SUCCESS;
            }
            catch (ObjectDisposedException) { return NTStatus.STATUS_FILE_CLOSED; }
            catch (Exception ex)
            {
                Console.WriteLine($"[ReadFile ERROR] {ex.Message}");
                return NTStatus.STATUS_DATA_ERROR;
            }
        }

        public NTStatus WriteFile(out int numberOfBytesWritten, object handle,
                long offset, byte[] data)
        {
            numberOfBytesWritten = 0;

            // Snapshot files are readonly
            if (handle is SnapshotFileHandle)
                return NTStatus.STATUS_ACCESS_DENIED;

            var h = handle as FileHandle;
            if (h == null || h.IsDirectory) return NTStatus.STATUS_INVALID_HANDLE;
            if (h.Stream == null) return NTStatus.STATUS_FILE_CLOSED;

            try
            {
                h.Stream.Position = offset;
                h.Stream.Write(data, 0, data.Length);
                h.Stream.Flush();
                numberOfBytesWritten = data.Length;

                h.WasDirty = true;  // ← NEU: Track that this handle wrote data

                return NTStatus.STATUS_SUCCESS;
            }
            catch (UnauthorizedAccessException) { return NTStatus.STATUS_ACCESS_DENIED; }
            catch (ObjectDisposedException) { return NTStatus.STATUS_FILE_CLOSED; }
            catch (IOException) { return NTStatus.STATUS_DATA_ERROR; }
        }


        // ===================== CLOSE =====================
        public NTStatus CloseFile(object handle)
        {
            // ── Snapshot handles: just close the stream ──
            if (handle is SnapshotFileHandle sfh)
            {
                sfh.Stream?.Dispose();
                return NTStatus.STATUS_SUCCESS;
            }
            if (handle is SnapshotDirectoryHandle)
            {
                return NTStatus.STATUS_SUCCESS;
            }

            var h = handle as FileHandle;
            if (h == null) return NTStatus.STATUS_INVALID_HANDLE;

            try
            {
                // ── Create version snapshot IF the file was actually written to ──
                // Three conditions must all be true:
                //   1. Versioning is enabled (_versionService != null)
                //   2. It's a file, not a directory
                //   3. WasDirty == true (at least one WriteFile call happened)
                //
                // This avoids:
                //   - Hashing files that were only read (perf waste)
                //   - Creating versions for directories
                //   - Creating versions when versioning is disabled
                //
                // The hash comparison inside CreateVersionAsync is the second guard:
                // if the content after all writes happens to be identical to the
                // last version (e.g. user saved without changes), it returns null.

                if (_serviceProvider != null && !h.IsDirectory && h.WasDirty && h.Stream != null && h.Stream.CanRead)
                {
                    try
                    {
                        using var scope = _serviceProvider.CreateScope();
                        var versionService = scope.ServiceProvider.GetRequiredService<IFileVersionService>();
                        h.Stream.Flush(true);
                        h.Stream.Position = 0;

                        var relativePath = GetRelativePath(h.Path);
                        var userId = h.User?.User?.Id.ToString();

                        // CreateVersionAsync handles:
                        //   - SHA-256 hashing
                        //   - Hash comparison → skip if unchanged
                        //   - Gzip compression
                        //   - CAS blob storage (dedup)
                        //   - DB record creation
                        //   - Retention policy
                        var version = Task.Run(() =>
                            versionService.CreateVersionAsync(relativePath, h.Stream, userId))
                            .GetAwaiter().GetResult();

                        if (version != null)
                            Console.WriteLine(
                                $"[Versioning] Created v{version.VersionNumber} for '{relativePath}' " +
                                $"at {version.SnapshotTimestampUtc:O} token={version.ToGmtToken()}");
                        else
                            Console.WriteLine($"[Versioning] Skipped for '{relativePath}' (unchanged or null)");
                    }
                    catch (Exception ex)
                    {
                        // Version creation failure must NEVER prevent file close.
                        // Log and continue the file itself is fine.
                        Console.WriteLine($"[Versioning] Failed for '{h.Path}': {ex.Message}");
                    }
                }

                h.Stream?.Dispose();

                if (h.DeleteOnClose)
                {
                    string relativePath = GetRelativePath(h.Path);
                    if (!CanDelete(relativePath, h.User))
                        return NTStatus.STATUS_ACCESS_DENIED;

                    if (h.IsDirectory && Directory.Exists(h.Path))
                        Directory.Delete(h.Path, true);
                    else if (!h.IsDirectory && File.Exists(h.Path))
                        File.Delete(h.Path);
                }

                return NTStatus.STATUS_SUCCESS;
            }
            catch { return NTStatus.STATUS_ACCESS_DENIED; }
        }

        public NTStatus FlushFileBuffers(object handle)
        {
            var h = handle as FileHandle;
            if (h?.Stream == null) return NTStatus.STATUS_INVALID_HANDLE;
            try { h.Stream.Flush(true); return NTStatus.STATUS_SUCCESS; }
            catch { return NTStatus.STATUS_DATA_ERROR; }
        }

        // ===================== DIRECTORY LISTING =====================
        public NTStatus QueryDirectory(out List<QueryDirectoryFileInformation> result,
    object handle, string fileName, FileInformationClass informationClass)
        {
            result = new List<QueryDirectoryFileInformation>();

            // ★ Handle snapshot directory listing
            if (handle is SnapshotDirectoryHandle sdh)
            {
                Console.WriteLine($"[QueryDirectory SNAPSHOT] path='{sdh.Path}', pattern='{fileName}', ts={sdh.SnapshotTimestamp:O}");

                try
                {
                    var dirInfo = new DirectoryInfo(sdh.Path);
                    if (!dirInfo.Exists) return NTStatus.STATUS_NO_SUCH_FILE;

                    string pattern = string.IsNullOrEmpty(fileName) ? "*" : fileName;
                    bool isWildcard = pattern == "*" || pattern == "*.*";

                    if (isWildcard)
                    {
                        result.Add(CreateFileInfoFromDir(".", dirInfo, informationClass));
                        result.Add(CreateFileInfoFromDir("..", dirInfo.Parent ?? dirInfo, informationClass));
                    }

                    foreach (var sub in dirInfo.GetDirectories())
                        if (MatchesPattern(sub.Name, pattern))
                            result.Add(CreateFileInfo(sub.Name, sub, true, informationClass));
                    foreach (var file in dirInfo.GetFiles())
                        if (MatchesPattern(file.Name, pattern))
                            result.Add(CreateFileInfo(file.Name, file, false, informationClass));

                    if (result.Count == 0) return NTStatus.STATUS_NO_SUCH_FILE;
                    return NTStatus.STATUS_SUCCESS;
                }
                catch (UnauthorizedAccessException) { return NTStatus.STATUS_ACCESS_DENIED; }
                catch (Exception ex)
                {
                    Console.WriteLine($"[QueryDirectory SNAPSHOT ERROR] {ex.Message}");
                    return NTStatus.STATUS_DATA_ERROR;
                }
            }

            var h = handle as FileHandle;
            if (h == null || !h.IsDirectory) return NTStatus.STATUS_INVALID_HANDLE;

            try
            {
                var dirInfo = new DirectoryInfo(h.Path);
                if (!dirInfo.Exists) return NTStatus.STATUS_NO_SUCH_FILE;

                string pattern = string.IsNullOrEmpty(fileName) ? "*" : fileName;
                bool isWildcard = pattern == "*" || pattern == "*.*";

                if (isWildcard)
                {
                    result.Add(CreateFileInfoFromDir(".", dirInfo, informationClass));
                    result.Add(CreateFileInfoFromDir("..",
                        dirInfo.Parent ?? dirInfo, informationClass));
                }

                foreach (var sub in dirInfo.GetDirectories())
                    if (MatchesPattern(sub.Name, pattern))
                        result.Add(CreateFileInfo(sub.Name, sub, true, informationClass));
                foreach (var file in dirInfo.GetFiles())
                    if (MatchesPattern(file.Name, pattern))
                        result.Add(CreateFileInfo(file.Name, file, false, informationClass));

                if (result.Count == 0) return NTStatus.STATUS_NO_SUCH_FILE;
                return NTStatus.STATUS_SUCCESS;
            }
            catch (UnauthorizedAccessException) { return NTStatus.STATUS_ACCESS_DENIED; }
            catch (Exception ex)
            {
                Console.WriteLine($"[QueryDirectory ERROR] {ex.Message}");
                return NTStatus.STATUS_DATA_ERROR;
            }
        }

        private static bool MatchesPattern(string name, string pattern)
        {
            if (pattern == "*" || pattern == "*.*") return true;
            string regexPattern = "^" +
                System.Text.RegularExpressions.Regex.Escape(pattern)
                    .Replace("\\*", ".*").Replace("\\?", ".") + "$";
            return System.Text.RegularExpressions.Regex.IsMatch(name, regexPattern,
                System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        }

        // ===================== FILE INFO =====================
        public NTStatus GetFileInformation(out FileInformation result, object handle,
    FileInformationClass informationClass)
        {
            result = null!;

            if (handle is SnapshotFileHandle sfh)
            {
                long size = sfh.Size;
                long allocSize = RoundUpAllocation(size);
                result = informationClass switch
                {
                    FileInformationClass.FileBasicInformation => new FileBasicInformation
                    {
                        CreationTime = sfh.SnapshotTimestamp,
                        LastWriteTime = sfh.SnapshotTimestamp,
                        LastAccessTime = sfh.SnapshotTimestamp,
                        ChangeTime = sfh.SnapshotTimestamp,
                        FileAttributes = FileAttributes.Normal | FileAttributes.ReadOnly
                    },
                    FileInformationClass.FileStandardInformation => new FileStandardInformation
                    {
                        AllocationSize = allocSize,
                        EndOfFile = size,
                        NumberOfLinks = 1,
                        DeletePending = false,
                        Directory = false
                    },
                    _ => new FileBasicInformation
                    {
                        CreationTime = sfh.SnapshotTimestamp,
                        LastWriteTime = sfh.SnapshotTimestamp,
                        LastAccessTime = sfh.SnapshotTimestamp,
                        ChangeTime = sfh.SnapshotTimestamp,
                        FileAttributes = FileAttributes.Normal | FileAttributes.ReadOnly
                    }
                };
                return NTStatus.STATUS_SUCCESS;
            }

            if (handle is SnapshotDirectoryHandle sdh)
            {
                result = informationClass switch
                {
                    FileInformationClass.FileBasicInformation => new FileBasicInformation
                    {
                        CreationTime = sdh.SnapshotTimestamp,
                        LastWriteTime = sdh.SnapshotTimestamp,
                        LastAccessTime = sdh.SnapshotTimestamp,
                        ChangeTime = sdh.SnapshotTimestamp,
                        FileAttributes = FileAttributes.Directory | FileAttributes.ReadOnly
                    },
                    FileInformationClass.FileStandardInformation => new FileStandardInformation
                    {
                        AllocationSize = 0,
                        EndOfFile = 0,
                        NumberOfLinks = 1,
                        DeletePending = false,
                        Directory = true
                    },
                    _ => new FileBasicInformation
                    {
                        CreationTime = sdh.SnapshotTimestamp,
                        LastWriteTime = sdh.SnapshotTimestamp,
                        LastAccessTime = sdh.SnapshotTimestamp,
                        ChangeTime = sdh.SnapshotTimestamp,
                        FileAttributes = FileAttributes.Directory | FileAttributes.ReadOnly
                    }
                };
                return NTStatus.STATUS_SUCCESS;
            }
            result = null!;
            var h = handle as FileHandle;
            if (h == null) return NTStatus.STATUS_INVALID_HANDLE;

            try
            {
                if (h.IsDirectory)
                {
                    var d = new DirectoryInfo(h.Path);
                    if (!d.Exists) return NTStatus.STATUS_OBJECT_NAME_NOT_FOUND;
                    result = BuildDirectoryInfo(d, h.DeleteOnClose, informationClass);
                }
                else
                {
                    var f = new FileInfo(h.Path);
                    if (!f.Exists) return NTStatus.STATUS_OBJECT_NAME_NOT_FOUND;
                    result = BuildFileInfo(f, h.DeleteOnClose, informationClass);
                }
                return NTStatus.STATUS_SUCCESS;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[GetFileInformation ERROR] {ex.Message}");
                return NTStatus.STATUS_DATA_ERROR;
            }
        }

        private static FileInformation BuildDirectoryInfo(DirectoryInfo d, bool deletePending,
            FileInformationClass cls) => cls switch
            {
                FileInformationClass.FileBasicInformation => new FileBasicInformation
                {
                    CreationTime = d.CreationTimeUtc,
                    LastWriteTime = d.LastWriteTimeUtc,
                    LastAccessTime = d.LastAccessTimeUtc,
                    ChangeTime = d.LastWriteTimeUtc,
                    FileAttributes = FileAttributes.Directory
                },
                FileInformationClass.FileStandardInformation => new FileStandardInformation
                {
                    AllocationSize = 0,
                    EndOfFile = 0,
                    NumberOfLinks = 1,
                    DeletePending = deletePending,
                    Directory = true
                },
                FileInformationClass.FileInternalInformation => new FileInternalInformation { IndexNumber = 0 },
                FileInformationClass.FileEaInformation => new FileEaInformation { EaSize = 0 },
                FileInformationClass.FileNetworkOpenInformation => new FileNetworkOpenInformation
                {
                    CreationTime = d.CreationTimeUtc,
                    LastWriteTime = d.LastWriteTimeUtc,
                    LastAccessTime = d.LastAccessTimeUtc,
                    ChangeTime = d.LastWriteTimeUtc,
                    AllocationSize = 0,
                    EndOfFile = 0,
                    FileAttributes = FileAttributes.Directory
                },
                FileInformationClass.FileAttributeTagInformation => new FileAttributeTagInformation
                { FileAttributes = FileAttributes.Directory, ReparsePointTag = 0 },
                _ => new FileBasicInformation
                {
                    CreationTime = d.CreationTimeUtc,
                    LastWriteTime = d.LastWriteTimeUtc,
                    LastAccessTime = d.LastAccessTimeUtc,
                    ChangeTime = d.LastWriteTimeUtc,
                    FileAttributes = FileAttributes.Directory
                }
            };

        private static FileInformation BuildFileInfo(FileInfo f, bool deletePending,
            FileInformationClass cls)
        {
            long fileSize = f.Length;
            long allocSize = RoundUpAllocation(fileSize);
            return cls switch
            {
                FileInformationClass.FileBasicInformation => new FileBasicInformation
                {
                    CreationTime = f.CreationTimeUtc,
                    LastWriteTime = f.LastWriteTimeUtc,
                    LastAccessTime = f.LastAccessTimeUtc,
                    ChangeTime = f.LastWriteTimeUtc,
                    FileAttributes = FileAttributes.Normal
                },
                FileInformationClass.FileStandardInformation => new FileStandardInformation
                {
                    AllocationSize = allocSize,
                    EndOfFile = fileSize,
                    NumberOfLinks = 1,
                    DeletePending = deletePending,
                    Directory = false
                },
                FileInformationClass.FileInternalInformation => new FileInternalInformation { IndexNumber = 0 },
                FileInformationClass.FileEaInformation => new FileEaInformation { EaSize = 0 },
                FileInformationClass.FileNetworkOpenInformation => new FileNetworkOpenInformation
                {
                    CreationTime = f.CreationTimeUtc,
                    LastWriteTime = f.LastWriteTimeUtc,
                    LastAccessTime = f.LastAccessTimeUtc,
                    ChangeTime = f.LastWriteTimeUtc,
                    AllocationSize = allocSize,
                    EndOfFile = fileSize,
                    FileAttributes = FileAttributes.Normal
                },
                FileInformationClass.FileAttributeTagInformation => new FileAttributeTagInformation
                { FileAttributes = FileAttributes.Normal, ReparsePointTag = 0 },
                FileInformationClass.FileStreamInformation =>
                    CreateFileStreamInformation(fileSize, allocSize),
                _ => new FileBasicInformation
                {
                    CreationTime = f.CreationTimeUtc,
                    LastWriteTime = f.LastWriteTimeUtc,
                    LastAccessTime = f.LastAccessTimeUtc,
                    ChangeTime = f.LastWriteTimeUtc,
                    FileAttributes = FileAttributes.Normal
                }
            };
        }

        // ===================== SET FILE INFO =====================
        public NTStatus SetFileInformation(object handle, FileInformation information)
        {
            var h = handle as FileHandle;
            if (h == null) return NTStatus.STATUS_INVALID_HANDLE;

            try
            {
                if (information is FileDispositionInformation disposition)
                {
                    h.DeleteOnClose = disposition.DeletePending;
                    return NTStatus.STATUS_SUCCESS;
                }

                if (information is FileRenameInformationType2 rename)
                {
                    string newPath = GetFullPath(rename.FileName);
                    if (h.IsDirectory)
                    {
                        if (Directory.Exists(newPath))
                            return NTStatus.STATUS_OBJECT_NAME_COLLISION;
                        Directory.Move(h.Path, newPath);
                    }
                    else
                    {
                        h.Stream?.Dispose();
                        h.Stream = null;
                        if (File.Exists(newPath) && !rename.ReplaceIfExists)
                            return NTStatus.STATUS_OBJECT_NAME_COLLISION;
                        if (File.Exists(newPath) && rename.ReplaceIfExists)
                            File.Delete(newPath);
                        File.Move(h.Path, newPath);
                        h.Stream = new FileStream(newPath, FileMode.Open,
                            FileAccess.ReadWrite, FileShare.ReadWrite);
                    }
                    h.Path = newPath;
                    return NTStatus.STATUS_SUCCESS;
                }

                if (information is FileBasicInformation basicInfo)
                {
                    if (h.IsDirectory)
                    {
                        var d = new DirectoryInfo(h.Path);
                        if (basicInfo.CreationTime.Time is { } ct && ct > DateTime.MinValue)
                            d.CreationTimeUtc = ct;
                        if (basicInfo.LastWriteTime.Time is { } wt && wt > DateTime.MinValue)
                            d.LastWriteTimeUtc = wt;
                        if (basicInfo.LastAccessTime.Time is { } at && at > DateTime.MinValue)
                            d.LastAccessTimeUtc = at;
                    }
                    else
                    {
                        var f = new FileInfo(h.Path);
                        if (basicInfo.CreationTime.Time is { } ct && ct > DateTime.MinValue)
                            f.CreationTimeUtc = ct;
                        if (basicInfo.LastWriteTime.Time is { } wt && wt > DateTime.MinValue)
                            f.LastWriteTimeUtc = wt;
                        if (basicInfo.LastAccessTime.Time is { } at && at > DateTime.MinValue)
                            f.LastAccessTimeUtc = at;
                    }
                    return NTStatus.STATUS_SUCCESS;
                }

                if (information is FileEndOfFileInformation eofInfo)
                {
                    if (h.Stream != null) h.Stream.SetLength(eofInfo.EndOfFile);
                    return NTStatus.STATUS_SUCCESS;
                }

                if (information is FileAllocationInformation allocInfo)
                {
                    if (h.Stream != null && h.Stream.Length > allocInfo.AllocationSize)
                        h.Stream.SetLength(allocInfo.AllocationSize);
                    return NTStatus.STATUS_SUCCESS;
                }

                return NTStatus.STATUS_NOT_SUPPORTED;
            }
            catch (UnauthorizedAccessException) { return NTStatus.STATUS_ACCESS_DENIED; }
            catch (IOException) { return NTStatus.STATUS_SHARING_VIOLATION; }
            catch (Exception ex)
            {
                Console.WriteLine($"[SetFileInformation ERROR] {ex.Message}");
                return NTStatus.STATUS_DATA_ERROR;
            }
        }

        // ===================== FILESYSTEM INFO =====================
        public NTStatus GetFileSystemInformation(out FileSystemInformation result,
            FileSystemInformationClass informationClass)
        {
            result = null!;
            try
            {
                var driveInfo = new DriveInfo(Path.GetPathRoot(_root) ?? _root);
                switch (informationClass)
                {
                    case FileSystemInformationClass.FileFsVolumeInformation:
                        result = new FileFsVolumeInformation
                        { VolumeLabel = "KaimoSMB", VolumeSerialNumber = 0x12345678 };
                        return NTStatus.STATUS_SUCCESS;
                    case FileSystemInformationClass.FileFsSizeInformation:
                        result = new FileFsSizeInformation
                        {
                            TotalAllocationUnits = driveInfo.TotalSize / 4096,
                            AvailableAllocationUnits = driveInfo.AvailableFreeSpace / 4096,
                            SectorsPerAllocationUnit = 8,
                            BytesPerSector = 512
                        };
                        return NTStatus.STATUS_SUCCESS;
                    case FileSystemInformationClass.FileFsFullSizeInformation:
                        result = new FileFsFullSizeInformation
                        {
                            TotalAllocationUnits = driveInfo.TotalSize / 4096,
                            CallerAvailableAllocationUnits = driveInfo.AvailableFreeSpace / 4096,
                            ActualAvailableAllocationUnits = driveInfo.AvailableFreeSpace / 4096,
                            SectorsPerAllocationUnit = 8,
                            BytesPerSector = 512
                        };
                        return NTStatus.STATUS_SUCCESS;
                    case FileSystemInformationClass.FileFsDeviceInformation:
                        result = new FileFsDeviceInformation
                        { DeviceType = DeviceType.Disk, Characteristics = (DeviceCharacteristics)0 };
                        return NTStatus.STATUS_SUCCESS;
                    case FileSystemInformationClass.FileFsAttributeInformation:
                        result = new FileFsAttributeInformation
                        {
                            FileSystemAttributes = FileSystemAttributes.UnicodeOnDisk
                                | FileSystemAttributes.CasePreservedNames,
                            MaximumComponentNameLength = 255,
                            FileSystemName = "NTFS"
                        };
                        return NTStatus.STATUS_SUCCESS;
                    default: return NTStatus.STATUS_INVALID_PARAMETER;
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[GetFileSystemInformation ERROR] {ex.Message}");
                result = new FileFsVolumeInformation { VolumeLabel = "KaimoSMB" };
                return NTStatus.STATUS_SUCCESS;
            }
        }

        // ===================== SECURITY =====================
        public NTStatus GetSecurityInformation(out SecurityDescriptor result, object handle,
            SecurityInformation securityInformation)
        { result = new SecurityDescriptor(); return NTStatus.STATUS_SUCCESS; }

        public NTStatus SetSecurityInformation(object handle,
            SecurityInformation securityInformation, SecurityDescriptor securityDescriptor)
            => NTStatus.STATUS_SUCCESS;

        // ===================== STUBS =====================
        public NTStatus Cancel(object ioRequest) => NTStatus.STATUS_SUCCESS;

        public NTStatus DeviceIOControl(object handle, uint ctlCode, byte[] input,
    out byte[] output, int maxOutputLength)
        {
            output = null!;

            Console.WriteLine($"[IOCTL] ctlCode=0x{ctlCode:X8}");

            if (ctlCode == SmbSnapshotHandler.FSCTL_SRV_ENUMERATE_SNAPSHOTS)
            {
                if (_serviceProvider == null)
                    return NTStatus.STATUS_NOT_SUPPORTED;

                try
                {
                    using var ioScope = _serviceProvider.CreateScope();
                    var versionService = ioScope.ServiceProvider.GetRequiredService<IFileVersionService>();

                    var timestamps = Task.Run(() =>
                        versionService.GetSnapshotTimestampsAsync())
                        .GetAwaiter().GetResult();

                    Console.WriteLine($"[IOCTL] ENUMERATE_SNAPSHOTS: {timestamps.Count} snapshots, maxOutput={maxOutputLength}");

                    if (maxOutputLength < 16)
                    {
                        // Not enough space even for the header
                        output = new byte[0];
                        return NTStatus.STATUS_BUFFER_TOO_SMALL;
                    }

                    // If client only asks for the header (16 bytes = 3x uint32 + padding),
                    // return just the counts so it knows how much to allocate
                    if (maxOutputLength < 32)
                    {
                        output = new byte[12];
                        var fullResponse = SmbSnapshotHandler.BuildEnumerateSnapshotsResponse(timestamps);
                        var snapshotArraySize = BitConverter.ToUInt32(fullResponse, 8);
                        BitConverter.GetBytes((uint)timestamps.Count).CopyTo(output, 0);
                        BitConverter.GetBytes((uint)0).CopyTo(output, 4);              // 0 returned
                        BitConverter.GetBytes(snapshotArraySize).CopyTo(output, 8);    // but tell full size
                        Console.WriteLine($"[IOCTL] Header-only response: {timestamps.Count} snapshots, array needs {snapshotArraySize} bytes");
                        return NTStatus.STATUS_SUCCESS;
                    }

                    // Full response
                    output = SmbSnapshotHandler.BuildEnumerateSnapshotsResponse(timestamps);

                    // Truncate if needed
                    if (output.Length > maxOutputLength)
                    {
                        Console.WriteLine($"[IOCTL] Truncating {output.Length} -> {maxOutputLength}");
                        Array.Resize(ref output, maxOutputLength);
                    }

                    Console.WriteLine($"[IOCTL] Full response: {output.Length} bytes");
                    return NTStatus.STATUS_SUCCESS;
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[IOCTL] ERROR: {ex.Message}");
                    return NTStatus.STATUS_NOT_SUPPORTED;
                }
            }

            return NTStatus.STATUS_NOT_SUPPORTED;
        }

        public NTStatus LockFile(object handle, long byteOffset, long length,
            bool exclusiveLock) => NTStatus.STATUS_SUCCESS;
        public NTStatus UnlockFile(object handle, long byteOffset, long length)
            => NTStatus.STATUS_SUCCESS;
        public NTStatus NotifyChange(out object ioRequest, object handle,
            NotifyChangeFilter completionFilter, bool watchTree, int outputBufferSize,
            OnNotifyChangeCompleted onNotifyChangeCompleted, object context)
        { ioRequest = null!; return NTStatus.STATUS_NOT_SUPPORTED; }
        public NTStatus SetFileSystemInformation(FileSystemInformation information)
            => NTStatus.STATUS_NOT_SUPPORTED;

        // ===================== HELPERS =====================
        private QueryDirectoryFileInformation CreateFileInfoFromDir(string name,
            DirectoryInfo dirInfo, FileInformationClass informationClass) =>
            informationClass switch
            {
                FileInformationClass.FileDirectoryInformation => new FileDirectoryInformation
                {
                    FileName = name,
                    CreationTime = dirInfo.CreationTimeUtc,
                    LastAccessTime = dirInfo.LastAccessTimeUtc,
                    LastWriteTime = dirInfo.LastWriteTimeUtc,
                    ChangeTime = dirInfo.LastWriteTimeUtc,
                    EndOfFile = 0,
                    AllocationSize = 0,
                    FileAttributes = FileAttributes.Directory
                },
                FileInformationClass.FileBothDirectoryInformation =>
                    new FileBothDirectoryInformation
                    {
                        FileName = name,
                        ShortName = name,
                        CreationTime = dirInfo.CreationTimeUtc,
                        LastAccessTime = dirInfo.LastAccessTimeUtc,
                        LastWriteTime = dirInfo.LastWriteTimeUtc,
                        ChangeTime = dirInfo.LastWriteTimeUtc,
                        EndOfFile = 0,
                        AllocationSize = 0,
                        FileAttributes = FileAttributes.Directory,
                        EaSize = 0
                    },
                FileInformationClass.FileIdBothDirectoryInformation =>
                    new FileIdBothDirectoryInformation
                    {
                        FileName = name,
                        ShortName = name,
                        CreationTime = dirInfo.CreationTimeUtc,
                        LastAccessTime = dirInfo.LastAccessTimeUtc,
                        LastWriteTime = dirInfo.LastWriteTimeUtc,
                        ChangeTime = dirInfo.LastWriteTimeUtc,
                        EndOfFile = 0,
                        AllocationSize = 0,
                        FileAttributes = FileAttributes.Directory,
                        EaSize = 0,
                        FileId = 0
                    },
                FileInformationClass.FileNamesInformation =>
                    new FileNamesInformation { FileName = name },
                _ => new FileDirectoryInformation
                {
                    FileName = name,
                    CreationTime = dirInfo.CreationTimeUtc,
                    LastAccessTime = dirInfo.LastAccessTimeUtc,
                    LastWriteTime = dirInfo.LastWriteTimeUtc,
                    ChangeTime = dirInfo.LastWriteTimeUtc,
                    EndOfFile = 0,
                    AllocationSize = 0,
                    FileAttributes = FileAttributes.Directory
                }
            };

        private QueryDirectoryFileInformation CreateFileInfo(string name,
            FileSystemInfo info, bool isDirectory,
            FileInformationClass informationClass)
        {
            var attrs = isDirectory ? FileAttributes.Directory : FileAttributes.Normal;
            long size = isDirectory ? 0 : ((FileInfo)info).Length;
            long allocSize = RoundUpAllocation(size);

            return informationClass switch
            {
                FileInformationClass.FileDirectoryInformation => new FileDirectoryInformation
                {
                    FileName = name,
                    CreationTime = info.CreationTimeUtc,
                    LastAccessTime = info.LastAccessTimeUtc,
                    LastWriteTime = info.LastWriteTimeUtc,
                    ChangeTime = info.LastWriteTimeUtc,
                    EndOfFile = size,
                    AllocationSize = allocSize,
                    FileAttributes = attrs
                },
                FileInformationClass.FileBothDirectoryInformation =>
                    new FileBothDirectoryInformation
                    {
                        FileName = name,
                        ShortName = GenerateShortName(name),
                        CreationTime = info.CreationTimeUtc,
                        LastAccessTime = info.LastAccessTimeUtc,
                        LastWriteTime = info.LastWriteTimeUtc,
                        ChangeTime = info.LastWriteTimeUtc,
                        EndOfFile = size,
                        AllocationSize = allocSize,
                        FileAttributes = attrs,
                        EaSize = 0
                    },
                FileInformationClass.FileIdBothDirectoryInformation =>
                    new FileIdBothDirectoryInformation
                    {
                        FileName = name,
                        ShortName = GenerateShortName(name),
                        CreationTime = info.CreationTimeUtc,
                        LastAccessTime = info.LastAccessTimeUtc,
                        LastWriteTime = info.LastWriteTimeUtc,
                        ChangeTime = info.LastWriteTimeUtc,
                        EndOfFile = size,
                        AllocationSize = allocSize,
                        FileAttributes = attrs,
                        EaSize = 0,
                        FileId = 0
                    },
                FileInformationClass.FileNamesInformation =>
                    new FileNamesInformation { FileName = name },
                _ => new FileDirectoryInformation
                {
                    FileName = name,
                    CreationTime = info.CreationTimeUtc,
                    LastAccessTime = info.LastAccessTimeUtc,
                    LastWriteTime = info.LastWriteTimeUtc,
                    ChangeTime = info.LastWriteTimeUtc,
                    EndOfFile = size,
                    AllocationSize = allocSize,
                    FileAttributes = attrs
                }
            };
        }

        private static string GenerateShortName(string name)
        {
            if (name.Length <= 12) return name;
            string ext = Path.GetExtension(name);
            string baseName = Path.GetFileNameWithoutExtension(name);
            if (ext.Length > 4) ext = ext.Substring(0, 4);
            int baseLen = Math.Min(baseName.Length, 6);
            return baseName.Substring(0, baseLen).ToUpperInvariant() + "~1"
                + ext.ToUpperInvariant();
        }

        private static long RoundUpAllocation(long size)
        {
            const long clusterSize = 4096;
            if (size == 0) return 0;
            return ((size + clusterSize - 1) / clusterSize) * clusterSize;
        }

        private static FileStreamInformation CreateFileStreamInformation(
            long fileSize, long allocSize)
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


        /// <summary>
        /// Opens a file from the version store for read-only access.
        /// Called when the path contains an @GMT- token (Windows "Previous Versions").
        /// </summary>
        private NTStatus OpenSnapshotFile(out object handle, out FileStatus fileStatus,
    string path, UserContext user)
        {
            handle = null!;
            fileStatus = FileStatus.FILE_DOES_NOT_EXIST;

            var snapshotInfo = SmbSnapshotHandler.ParseSnapshotPath(path);
            if (snapshotInfo == null)
                return NTStatus.STATUS_OBJECT_NAME_NOT_FOUND;

            try
            {
                // ★ FIX 1: Normalize AND actually use the normalized path
                var realPath = snapshotInfo.RealPath.Replace('\\', '/');

                if (string.IsNullOrEmpty(realPath))
                {
                    handle = new SnapshotDirectoryHandle
                    {
                        Path = _root,
                        SnapshotTimestamp = snapshotInfo.SnapshotTimestamp,
                        User = user
                    };
                    fileStatus = FileStatus.FILE_OPENED;
                    return NTStatus.STATUS_SUCCESS;
                }

                if (!CanRead(realPath, user))
                    return NTStatus.STATUS_ACCESS_DENIED;

                // ★ FIX 2: Resolve versionService per scope instead of _versionService
                using var snapshotScope = _serviceProvider!.CreateScope();
                var versionService = snapshotScope.ServiceProvider
                    .GetRequiredService<IFileVersionService>();

                Console.WriteLine($"[OpenSnapshotFile] Looking up '{realPath}' at {snapshotInfo.SnapshotTimestamp:O}");

                var stream = Task.Run(() =>
                    versionService.ReadVersionAsync(realPath, snapshotInfo.SnapshotTimestamp))
                    .GetAwaiter().GetResult();

                handle = new SnapshotFileHandle
                {
                    Stream = stream,
                    Path = realPath,
                    SnapshotTimestamp = snapshotInfo.SnapshotTimestamp,
                    User = user,
                    Size = stream.Length
                };

                fileStatus = FileStatus.FILE_OPENED;
                return NTStatus.STATUS_SUCCESS;
            }
            catch (FileNotFoundException)
            {
                return NTStatus.STATUS_OBJECT_NAME_NOT_FOUND;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[OpenSnapshotFile ERROR] {path}: {ex.Message}");
                return NTStatus.STATUS_OBJECT_NAME_NOT_FOUND;
            }
        }

        // ── Snapshot handle types (readonly, separate from FileHandle) ──

        private sealed class SnapshotFileHandle
        {
            public Stream Stream { get; init; } = null!;
            public string Path { get; init; } = "";
            public DateTime SnapshotTimestamp { get; init; }
            public UserContext User { get; init; } = null!;
            public long Size { get; init; }
        }

        private sealed class SnapshotDirectoryHandle
        {
            public string Path { get; init; } = "";
            public DateTime SnapshotTimestamp { get; init; }
            public UserContext User { get; init; } = null!;
        }


    }


}
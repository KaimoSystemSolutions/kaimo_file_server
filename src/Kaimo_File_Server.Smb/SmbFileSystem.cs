using Kaimo_File_Server.Core.Domain.Identity;
using Kaimo_File_Server.Core.Helpers;
using Kaimo_File_Server.Core.Services.File;
using Microsoft.Extensions.DependencyInjection;
using SMBLibrary;
using SMBLibrary.Server;
using FileAttributes = SMBLibrary.FileAttributes;

namespace Kaimo_File_Server.Smb;

public class SmbFileSystem : INTFileStore
{
    private readonly string _root;
    private readonly string _shareName;
    private readonly IFileService _fileService;
    private readonly IServiceProvider? _serviceProvider;

    public SmbFileSystem(
        string rootPath, string shareName, IFileService fileService,
        IServiceProvider? serviceProvider = null)
    {
        _root = rootPath;
        _shareName = shareName;
        _fileService = fileService;
        _serviceProvider = serviceProvider;
        Directory.CreateDirectory(_root);
    }

    // ------------------ Per-session user context ------------------

    private static readonly AsyncLocal<UserContext?> _sessionUser = new();

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, UserContext>
        _userContextFallback = new(StringComparer.OrdinalIgnoreCase);

    private const int MaxCachedUsers = 256;

    public static void SetSessionUser(UserContext user)
    {
        _sessionUser.Value = user;

        var name = user?.User?.Username;
        if (!string.IsNullOrEmpty(name))
        {
            if (_userContextFallback.Count >= MaxCachedUsers)
                _userContextFallback.Clear();

            _userContextFallback[name] = user;
        }
    }

    public static void RestoreSessionFromFallback(string username)
    {
        if (_sessionUser.Value != null)
            return;

        if (username != null && _userContextFallback.TryGetValue(username, out var cached))
            _sessionUser.Value = cached;
    }

    /// <summary>
    /// Returns the current session user, or null if none is set.
    /// Used by SmbServer for ABE filtering during share enumeration,
    /// where throwing on missing context would be wrong.
    /// </summary>
    public static UserContext? GetSessionUserOrDefault()
    {
        return _sessionUser.Value;
    }

    private UserContext RequireSessionUser(SecurityContext? securityContext = null)
    {
        if (_sessionUser.Value != null)
            return _sessionUser.Value;

        if (securityContext?.UserName != null &&
            _userContextFallback.TryGetValue(securityContext.UserName, out var cached))
        {
            _sessionUser.Value = cached;
            return cached;
        }

        throw new InvalidOperationException(
            "No authenticated user context. SetSessionUser must be called before filesystem operations.");
    }

    // ------------------ Path helpers ------------------

    private string ToAbsolutePath(string smbPath)
    {
        var cleaned = smbPath
            .Replace('\\', Path.DirectorySeparatorChar)
            .TrimStart(Path.DirectorySeparatorChar);

        var root = Path.GetFullPath(_root)
            .TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var full = Path.GetFullPath(Path.Combine(root, cleaned));

        if (!full.StartsWith(root, StringComparison.OrdinalIgnoreCase))
            throw new UnauthorizedAccessException("Path traversal detected");

        return full;
    }

    private string ToShareRelativePath(string absolutePath)
    {
        var root = Path.GetFullPath(_root)
            .TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var full = Path.GetFullPath(absolutePath);

        if (!full.StartsWith(root, StringComparison.OrdinalIgnoreCase))
            return ShareRelativePath.Normalize(absolutePath);

        var relative = full[root.Length..];
        return ShareRelativePath.Normalize(relative);
    }

    // ------------------ Handle types ------------------

    private sealed class FileHandle
    {
        public FileStream? Stream;
        public string AbsolutePath = "";
        public bool IsDirectory;
        public bool DeleteOnClose;
        public UserContext User { get; init; } = null!;
        public bool WasDirty { get; set; }
    }

    private sealed class SnapshotFileHandle
    {
        public Stream Stream { get; init; } = null!;
        public string RelativePath { get; init; } = "";
        public DateTime SnapshotTimestamp { get; init; }
        public UserContext User { get; init; } = null!;
        public long Size { get; init; }
    }

    private sealed class SnapshotDirectoryHandle
    {
        public string AbsolutePath { get; init; } = "";
        public DateTime SnapshotTimestamp { get; init; }
        public UserContext User { get; init; } = null!;
    }

    // ------------------ Permission wrappers ------------------

    private bool CanRead(string relativePath, UserContext user)
        => Task.Run(() => _fileService.CanReadAsync(relativePath, user)).GetAwaiter().GetResult();

    private bool CanWrite(string relativePath, UserContext user)
        => Task.Run(() => _fileService.CanWriteAsync(relativePath, user)).GetAwaiter().GetResult();

    private bool CanCreate(string relativePath, UserContext user)
        => Task.Run(() => _fileService.CanCreateAsync(relativePath, user)).GetAwaiter().GetResult();

    private bool CanDelete(string relativePath, UserContext user)
        => Task.Run(() => _fileService.CanDeleteAsync(relativePath, user)).GetAwaiter().GetResult();

    private bool CanList(string relativePath, UserContext user)
        => Task.Run(() => _fileService.CanListAsync(relativePath, user)).GetAwaiter().GetResult();

    private HashSet<string> FilterReadablePaths(
        IReadOnlyList<(string relativePath, bool isDirectory)> items, UserContext user)
        => Task.Run(() => _fileService.FilterReadablePathsAsync(items, user)).GetAwaiter().GetResult();

    private bool EnsureShareAccess(UserContext user)
    {
        return CanList("", user);
    }

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
            var user = RequireSessionUser(securityContext);

            if (!EnsureShareAccess(user))
                return NTStatus.STATUS_ACCESS_DENIED;

            if (SmbSnapshotHandler.IsSnapshotPath(path) && _serviceProvider != null)
                return OpenSnapshotFile(out handle, out fileStatus, path, user);

            string absolutePath = ToAbsolutePath(path);
            string relativePath = ToShareRelativePath(absolutePath);
            bool isDirectory = (createOptions & CreateOptions.FILE_DIRECTORY_FILE) != 0;
            if (!isDirectory && Directory.Exists(absolutePath))
                isDirectory = true;

            if (isDirectory)
                return CreateDirectory(out handle, out fileStatus, absolutePath, relativePath,
                    createDisposition, createOptions, user);

            return CreateRegularFile(out handle, out fileStatus, absolutePath, relativePath,
                createDisposition, createOptions, desiredAccess, shareAccess, user);
        }
        catch (InvalidOperationException) { throw; }
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
        string absolutePath, string relativePath,
        CreateDisposition createDisposition, CreateOptions createOptions,
        UserContext user)
    {
        handle = null!;
        fileStatus = FileStatus.FILE_DOES_NOT_EXIST;

        switch (createDisposition)
        {
            case CreateDisposition.FILE_CREATE:
                if (Directory.Exists(absolutePath))
                { fileStatus = FileStatus.FILE_EXISTS; return NTStatus.STATUS_OBJECT_NAME_COLLISION; }
                if (!CanCreate(relativePath, user))
                    return NTStatus.STATUS_ACCESS_DENIED;
                Directory.CreateDirectory(absolutePath);
                fileStatus = FileStatus.FILE_CREATED;
                break;

            case CreateDisposition.FILE_OPEN:
                if (!Directory.Exists(absolutePath))
                { fileStatus = FileStatus.FILE_DOES_NOT_EXIST; return NTStatus.STATUS_OBJECT_PATH_NOT_FOUND; }
                if (!CanRead(relativePath, user))
                    return NTStatus.STATUS_ACCESS_DENIED;
                fileStatus = FileStatus.FILE_OPENED;
                break;

            default:
                if (!Directory.Exists(absolutePath))
                {
                    if (!CanCreate(relativePath, user))
                        return NTStatus.STATUS_ACCESS_DENIED;
                    Directory.CreateDirectory(absolutePath);
                    fileStatus = FileStatus.FILE_CREATED;
                }
                else
                {
                    if (!CanRead(relativePath, user))
                        return NTStatus.STATUS_ACCESS_DENIED;
                    fileStatus = FileStatus.FILE_OPENED;
                }
                break;
        }

        handle = new FileHandle
        {
            AbsolutePath = absolutePath,
            IsDirectory = true,
            User = user,
            DeleteOnClose = (createOptions & CreateOptions.FILE_DELETE_ON_CLOSE) != 0
        };
        return NTStatus.STATUS_SUCCESS;
    }

    private NTStatus CreateRegularFile(
        out object handle, out FileStatus fileStatus,
        string absolutePath, string relativePath,
        CreateDisposition createDisposition, CreateOptions createOptions,
        AccessMask desiredAccess, ShareAccess shareAccess, UserContext user)
    {
        handle = null!;
        fileStatus = FileStatus.FILE_DOES_NOT_EXIST;

        string? parentDir = Path.GetDirectoryName(absolutePath);
        if (parentDir != null && !Directory.Exists(parentDir))
            return NTStatus.STATUS_OBJECT_PATH_NOT_FOUND;

        bool exists = File.Exists(absolutePath);

        switch (createDisposition)
        {
            case CreateDisposition.FILE_OPEN:
            case CreateDisposition.FILE_OVERWRITE:
                if (!exists) return NTStatus.STATUS_OBJECT_NAME_NOT_FOUND;
                break;
            case CreateDisposition.FILE_CREATE:
                if (exists) { fileStatus = FileStatus.FILE_EXISTS; return NTStatus.STATUS_OBJECT_NAME_COLLISION; }
                break;
        }

        FileAccess fileAccess = MapFileAccess(desiredAccess);
        bool requestsWrite = createDisposition != CreateDisposition.FILE_OPEN
            || fileAccess is FileAccess.Write or FileAccess.ReadWrite;

        if (!exists)
        {
            if (!CanCreate(relativePath, user))
                return NTStatus.STATUS_ACCESS_DENIED;
        }
        else if (requestsWrite)
        {
            if (!CanWrite(relativePath, user))
                return NTStatus.STATUS_ACCESS_DENIED;
        }
        else
        {
            if (!CanRead(relativePath, user))
                return NTStatus.STATUS_ACCESS_DENIED;
        }

        if ((createOptions & CreateOptions.FILE_DELETE_ON_CLOSE) != 0)
        {
            if (!CanDelete(relativePath, user))
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

        FileShare fileShare = MapFileShare(shareAccess);
        var fs = new FileStream(absolutePath, mode, fileAccess, fileShare);

        fileStatus = (createDisposition, exists) switch
        {
            (CreateDisposition.FILE_SUPERSEDE, true) => FileStatus.FILE_SUPERSEDED,
            (CreateDisposition.FILE_OVERWRITE, true) => FileStatus.FILE_OVERWRITTEN,
            (CreateDisposition.FILE_OVERWRITE_IF, true) => FileStatus.FILE_OVERWRITTEN,
            (_, false) => FileStatus.FILE_CREATED,
            _ => FileStatus.FILE_OPENED,
        };

        handle = new FileHandle
        {
            Stream = fs,
            AbsolutePath = absolutePath,
            IsDirectory = false,
            User = user,
            DeleteOnClose = (createOptions & CreateOptions.FILE_DELETE_ON_CLOSE) != 0
        };
        return NTStatus.STATUS_SUCCESS;
    }

    // ═══════════════════════ READ / WRITE ═══════════════════════

    public NTStatus ReadFile(out byte[] data, object handle, long offset, int maxCount)
    {
        data = null!;

        if (handle is SnapshotFileHandle sfh)
            return ReadFromStream(out data, sfh.Stream, offset, maxCount);

        var h = handle as FileHandle;
        if (h == null || h.IsDirectory) return NTStatus.STATUS_INVALID_HANDLE;
        if (h.Stream == null) return NTStatus.STATUS_FILE_CLOSED;

        return ReadFromStream(out data, h.Stream, offset, maxCount);
    }

    private static NTStatus ReadFromStream(out byte[] data, Stream stream, long offset, int maxCount)
    {
        data = null!;
        try
        {
            stream.Position = offset;
            byte[] buffer = new byte[maxCount];
            int read = stream.Read(buffer, 0, maxCount);
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
            h.WasDirty = true;
            return NTStatus.STATUS_SUCCESS;
        }
        catch (UnauthorizedAccessException) { return NTStatus.STATUS_ACCESS_DENIED; }
        catch (ObjectDisposedException) { return NTStatus.STATUS_FILE_CLOSED; }
        catch (IOException) { return NTStatus.STATUS_DATA_ERROR; }
    }

    // ═══════════════════════ CLOSE ═══════════════════════

    public NTStatus CloseFile(object handle)
    {
        if (handle is SnapshotFileHandle sfh)
        { sfh.Stream?.Dispose(); return NTStatus.STATUS_SUCCESS; }

        if (handle is SnapshotDirectoryHandle)
            return NTStatus.STATUS_SUCCESS;

        var h = handle as FileHandle;
        if (h == null) return NTStatus.STATUS_INVALID_HANDLE;

        try
        {
            if (_serviceProvider != null && !h.IsDirectory && h.WasDirty && h.Stream is { CanRead: true })
                CreateVersionOnClose(h);

            h.Stream?.Dispose();

            if (h.DeleteOnClose)
            {
                string relativePath = ToShareRelativePath(h.AbsolutePath);
                if (!CanDelete(relativePath, h.User))
                    return NTStatus.STATUS_ACCESS_DENIED;

                if (h.IsDirectory && Directory.Exists(h.AbsolutePath))
                    Directory.Delete(h.AbsolutePath, true);
                else if (!h.IsDirectory && File.Exists(h.AbsolutePath))
                    File.Delete(h.AbsolutePath);
            }

            return NTStatus.STATUS_SUCCESS;
        }
        catch { return NTStatus.STATUS_ACCESS_DENIED; }
    }

    private void CreateVersionOnClose(FileHandle h)
    {
        try
        {
            using var scope = _serviceProvider!.CreateScope();
            var versionService = scope.ServiceProvider.GetRequiredService<IFileVersionService>();

            h.Stream!.Flush(true);
            h.Stream.Position = 0;

            var relativePath = ToShareRelativePath(h.AbsolutePath);
            var userId = h.User?.User?.Id.ToString();

            var version = Task.Run(() =>
                versionService.CreateVersionAsync(relativePath, h.Stream, userId))
                .GetAwaiter().GetResult();

            if (version != null)
                Console.WriteLine(
                    $"[Versioning] Created v{version.VersionNumber} for '{relativePath}' " +
                    $"at {version.SnapshotTimestampUtc:O}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Versioning] Failed for '{h.AbsolutePath}': {ex.Message}");
        }
    }

    public NTStatus FlushFileBuffers(object handle)
    {
        var h = handle as FileHandle;
        if (h?.Stream == null) return NTStatus.STATUS_INVALID_HANDLE;
        try { h.Stream.Flush(true); return NTStatus.STATUS_SUCCESS; }
        catch { return NTStatus.STATUS_DATA_ERROR; }
    }

    // ═══════════════════════ SNAPSHOT ACCESS ═══════════════════════

    private NTStatus OpenSnapshotFile(
        out object handle, out FileStatus fileStatus,
        string path, UserContext user)
    {
        handle = null!;
        fileStatus = FileStatus.FILE_DOES_NOT_EXIST;

        var snapshotInfo = SmbSnapshotHandler.ParseSnapshotPath(path);
        if (snapshotInfo == null)
            return NTStatus.STATUS_OBJECT_NAME_NOT_FOUND;

        try
        {
            var relativePath = ShareRelativePath.Normalize(snapshotInfo.RealPath);

            if (string.IsNullOrEmpty(relativePath))
            {
                handle = new SnapshotDirectoryHandle
                {
                    AbsolutePath = _root,
                    SnapshotTimestamp = snapshotInfo.SnapshotTimestamp,
                    User = user
                };
                fileStatus = FileStatus.FILE_OPENED;
                return NTStatus.STATUS_SUCCESS;
            }

            if (!CanRead(relativePath, user))
                return NTStatus.STATUS_ACCESS_DENIED;

            using var scope = _serviceProvider!.CreateScope();
            var versionService = scope.ServiceProvider.GetRequiredService<IFileVersionService>();

            var stream = Task.Run(() =>
                versionService.ReadVersionAsync(relativePath, snapshotInfo.SnapshotTimestamp))
                .GetAwaiter().GetResult();

            handle = new SnapshotFileHandle
            {
                Stream = stream,
                RelativePath = relativePath,
                SnapshotTimestamp = snapshotInfo.SnapshotTimestamp,
                User = user,
                Size = stream.Length
            };

            fileStatus = FileStatus.FILE_OPENED;
            return NTStatus.STATUS_SUCCESS;
        }
        catch (FileNotFoundException) { return NTStatus.STATUS_OBJECT_NAME_NOT_FOUND; }
        catch (Exception ex)
        {
            Console.WriteLine($"[OpenSnapshotFile ERROR] {path}: {ex.Message}");
            return NTStatus.STATUS_OBJECT_NAME_NOT_FOUND;
        }
    }

    // ═══════════════════════ DIRECTORY LISTING ═══════════════════════

    public NTStatus QueryDirectory(
        out List<QueryDirectoryFileInformation> result,
        object handle, string fileName, FileInformationClass informationClass)
    {
        result = new List<QueryDirectoryFileInformation>();

        if (handle is SnapshotDirectoryHandle sdh)
            return QueryDirectoryImpl(out result, sdh.AbsolutePath, fileName, informationClass, sdh.User);

        var h = handle as FileHandle;
        if (h == null || !h.IsDirectory) return NTStatus.STATUS_INVALID_HANDLE;

        string dirRelativePath = ToShareRelativePath(h.AbsolutePath);
        if (!CanList(dirRelativePath, h.User))
            return NTStatus.STATUS_ACCESS_DENIED;

        return QueryDirectoryImpl(out result, h.AbsolutePath, fileName, informationClass, h.User);
    }

    private NTStatus QueryDirectoryImpl(
        out List<QueryDirectoryFileInformation> result,
        string absoluteDirPath, string fileName, FileInformationClass informationClass,
        UserContext user)
    {
        result = new List<QueryDirectoryFileInformation>();

        try
        {
            var dirInfo = new DirectoryInfo(absoluteDirPath);
            if (!dirInfo.Exists) return NTStatus.STATUS_NO_SUCH_FILE;

            string pattern = string.IsNullOrEmpty(fileName) ? "*" : fileName;
            bool isWildcard = pattern is "*" or "*.*";

            if (isWildcard)
            {
                result.Add(CreateDirEntryInfo(".", dirInfo, informationClass));
                result.Add(CreateDirEntryInfo("..", dirInfo.Parent ?? dirInfo, informationClass));
            }

            string dirRelativePath = ToShareRelativePath(absoluteDirPath);

            var candidates = new List<(FileSystemInfo info, string name, bool isDir, string relativePath)>();

            foreach (var sub in dirInfo.GetDirectories())
            {
                if (!MatchesPattern(sub.Name, pattern)) continue;
                var rel = ShareRelativePath.Combine(dirRelativePath, sub.Name);
                candidates.Add((sub, sub.Name, true, rel));
            }

            foreach (var file in dirInfo.GetFiles())
            {
                if (!MatchesPattern(file.Name, pattern)) continue;
                var rel = ShareRelativePath.Combine(dirRelativePath, file.Name);
                candidates.Add((file, file.Name, false, rel));
            }

            if (candidates.Count > 0)
            {
                var itemsToCheck = candidates
                    .Select(c => (c.relativePath, c.isDir))
                    .ToList();

                var readable = FilterReadablePaths(itemsToCheck, user);

                foreach (var (info, name, isDir, relativePath) in candidates)
                {
                    if (readable.Contains(relativePath))
                        result.Add(CreateEntryInfo(name, info, isDir, informationClass));
                }
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

    private static bool MatchesPattern(string name, string pattern)
    {
        if (pattern is "*" or "*.*") return true;
        string regexPattern = "^" +
            System.Text.RegularExpressions.Regex.Escape(pattern)
                .Replace("\\*", ".*").Replace("\\?", ".") + "$";
        return System.Text.RegularExpressions.Regex.IsMatch(
            name, regexPattern,
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);
    }

    // ═══════════════════════ FILE INFO ═══════════════════════

    public NTStatus GetFileInformation(
        out FileInformation result, object handle,
        FileInformationClass informationClass)
    {
        result = null!;

        if (handle is SnapshotFileHandle sfh)
        {
            result = BuildSnapshotFileInfo(sfh.Size, sfh.SnapshotTimestamp, false, informationClass);
            return NTStatus.STATUS_SUCCESS;
        }

        if (handle is SnapshotDirectoryHandle sdh)
        {
            result = BuildSnapshotFileInfo(0, sdh.SnapshotTimestamp, true, informationClass);
            return NTStatus.STATUS_SUCCESS;
        }

        var h = handle as FileHandle;
        if (h == null) return NTStatus.STATUS_INVALID_HANDLE;

        try
        {
            if (h.IsDirectory)
            {
                var d = new DirectoryInfo(h.AbsolutePath);
                if (!d.Exists) return NTStatus.STATUS_OBJECT_NAME_NOT_FOUND;
                result = BuildDirectoryInfo(d, h.DeleteOnClose, informationClass);
            }
            else
            {
                var f = new FileInfo(h.AbsolutePath);
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

    // ═══════════════════════ SET FILE INFO ═══════════════════════

    public NTStatus SetFileInformation(object handle, FileInformation information)
    {
        var h = handle as FileHandle;
        if (h == null) return NTStatus.STATUS_INVALID_HANDLE;

        try
        {
            if (information is FileDispositionInformation disposition)
            {
                if (disposition.DeletePending)
                {
                    string relativePath = ToShareRelativePath(h.AbsolutePath);
                    if (!CanDelete(relativePath, h.User))
                        return NTStatus.STATUS_ACCESS_DENIED;
                }
                h.DeleteOnClose = disposition.DeletePending;
                return NTStatus.STATUS_SUCCESS;
            }

            if (information is FileRenameInformationType2 rename)
                return HandleRename(h, rename);

            if (information is FileBasicInformation basicInfo)
                return HandleSetBasicInfo(h, basicInfo);

            if (information is FileEndOfFileInformation eofInfo)
            {
                h.Stream?.SetLength(eofInfo.EndOfFile);
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

    private NTStatus HandleRename(FileHandle h, FileRenameInformationType2 rename)
    {
        string newAbsPath = ToAbsolutePath(rename.FileName);
        string oldRelativePath = ToShareRelativePath(h.AbsolutePath);
        string newRelativePath = ToShareRelativePath(newAbsPath);

        if (!CanDelete(oldRelativePath, h.User))
            return NTStatus.STATUS_ACCESS_DENIED;

        if (!CanCreate(newRelativePath, h.User))
            return NTStatus.STATUS_ACCESS_DENIED;

        if (h.IsDirectory)
        {
            if (Directory.Exists(newAbsPath))
                return NTStatus.STATUS_OBJECT_NAME_COLLISION;
            Directory.Move(h.AbsolutePath, newAbsPath);
            h.AbsolutePath = newAbsPath;
            UpdateAclPathsOnRename(oldRelativePath, newRelativePath);
            return NTStatus.STATUS_SUCCESS;
        }

        if (File.Exists(newAbsPath) && !rename.ReplaceIfExists)
            return NTStatus.STATUS_OBJECT_NAME_COLLISION;

        if (File.Exists(newAbsPath) && rename.ReplaceIfExists)
        {
            if (!CanDelete(newRelativePath, h.User))
                return NTStatus.STATUS_ACCESS_DENIED;
        }

        var oldPath = h.AbsolutePath;
        h.Stream?.Dispose();
        h.Stream = null;

        try
        {
            if (File.Exists(newAbsPath) && rename.ReplaceIfExists)
                File.Delete(newAbsPath);

            File.Move(oldPath, newAbsPath);
            h.AbsolutePath = newAbsPath;
            h.Stream = new FileStream(newAbsPath, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite);
            UpdateAclPathsOnRename(oldRelativePath, newRelativePath);
            return NTStatus.STATUS_SUCCESS;
        }
        catch (Exception)
        {
            try
            {
                if (File.Exists(oldPath))
                    h.Stream = new FileStream(oldPath, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite);
            }
            catch { /* Stream bleibt null — CloseFile handled das */ }

            return NTStatus.STATUS_ACCESS_DENIED;
        }
    }

    private void UpdateAclPathsOnRename(string oldRelativePath, string newRelativePath)
    {
        try
        {
            Task.Run(() => _fileService.RenameAsync(oldRelativePath, newRelativePath,
                RequireSessionUser())).GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            Console.WriteLine(
                $"[ACL Rename] Failed to update ACL paths '{oldRelativePath}' → '{newRelativePath}': {ex.Message}");
        }
    }

    private static NTStatus HandleSetBasicInfo(FileHandle h, FileBasicInformation basicInfo)
    {
        if (h.IsDirectory)
        {
            var d = new DirectoryInfo(h.AbsolutePath);
            if (basicInfo.CreationTime.Time is { } ct && ct > DateTime.MinValue) d.CreationTimeUtc = ct;
            if (basicInfo.LastWriteTime.Time is { } wt && wt > DateTime.MinValue) d.LastWriteTimeUtc = wt;
            if (basicInfo.LastAccessTime.Time is { } at && at > DateTime.MinValue) d.LastAccessTimeUtc = at;
        }
        else
        {
            var f = new FileInfo(h.AbsolutePath);
            if (basicInfo.CreationTime.Time is { } ct && ct > DateTime.MinValue) f.CreationTimeUtc = ct;
            if (basicInfo.LastWriteTime.Time is { } wt && wt > DateTime.MinValue) f.LastWriteTimeUtc = wt;
            if (basicInfo.LastAccessTime.Time is { } at && at > DateTime.MinValue) f.LastAccessTimeUtc = at;
        }
        return NTStatus.STATUS_SUCCESS;
    }

    // ═══════════════════════ FILESYSTEM INFO ═══════════════════════

    public NTStatus GetFileSystemInformation(
        out FileSystemInformation result,
        FileSystemInformationClass informationClass)
    {
        result = null!;
        try
        {
            var driveInfo = new DriveInfo(Path.GetPathRoot(_root) ?? _root);
            result = informationClass switch
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
                        FileSystemAttributes = FileSystemAttributes.UnicodeOnDisk | FileSystemAttributes.CasePreservedNames,
                        MaximumComponentNameLength = 255,
                        FileSystemName = "NTFS"
                    },

                _ => null!
            };

            return result != null ? NTStatus.STATUS_SUCCESS : NTStatus.STATUS_INVALID_PARAMETER;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[GetFileSystemInformation ERROR] {ex.Message}");
            result = new FileFsVolumeInformation { VolumeLabel = "KaimoSMB" };
            return NTStatus.STATUS_SUCCESS;
        }
    }

    // ═══════════════════════ SECURITY ═══════════════════════

    public NTStatus GetSecurityInformation(
        out SecurityDescriptor result, object handle,
        SecurityInformation securityInformation)
    { result = new SecurityDescriptor(); return NTStatus.STATUS_SUCCESS; }

    public NTStatus SetSecurityInformation(
        object handle, SecurityInformation securityInformation,
        SecurityDescriptor securityDescriptor)
        => NTStatus.STATUS_SUCCESS;

    // ═══════════════════════ IOCTL ═══════════════════════

    public NTStatus DeviceIOControl(
        object handle, uint ctlCode, byte[] input,
        out byte[] output, int maxOutputLength)
    {
        output = null!;

        if (ctlCode == SmbSnapshotHandler.FSCTL_SRV_ENUMERATE_SNAPSHOTS)
            return HandleEnumerateSnapshots(out output, maxOutputLength);

        return NTStatus.STATUS_NOT_SUPPORTED;
    }

    private NTStatus HandleEnumerateSnapshots(out byte[] output, int maxOutputLength)
    {
        output = null!;

        if (_serviceProvider == null)
            return NTStatus.STATUS_NOT_SUPPORTED;

        try
        {
            using var scope = _serviceProvider.CreateScope();
            var versionService = scope.ServiceProvider.GetRequiredService<IFileVersionService>();

            var timestamps = Task.Run(() =>
                versionService.GetSnapshotTimestampsAsync())
                .GetAwaiter().GetResult();

            if (maxOutputLength < 16)
                return NTStatus.STATUS_BUFFER_TOO_SMALL;

            if (maxOutputLength < 32)
            {
                output = new byte[12];
                var fullResponse = SmbSnapshotHandler.BuildEnumerateSnapshotsResponse(timestamps);
                var snapshotArraySize = BitConverter.ToUInt32(fullResponse, 8);
                BitConverter.GetBytes((uint)timestamps.Count).CopyTo(output, 0);
                BitConverter.GetBytes((uint)0).CopyTo(output, 4);
                BitConverter.GetBytes(snapshotArraySize).CopyTo(output, 8);
                return NTStatus.STATUS_SUCCESS;
            }

            output = SmbSnapshotHandler.BuildEnumerateSnapshotsResponse(timestamps);
            if (output.Length > maxOutputLength)
                Array.Resize(ref output, maxOutputLength);

            return NTStatus.STATUS_SUCCESS;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[IOCTL ENUMERATE_SNAPSHOTS ERROR] {ex.Message}");
            return NTStatus.STATUS_NOT_SUPPORTED;
        }
    }

    // ═══════════════════════ STUBS ═══════════════════════

    public NTStatus Cancel(object ioRequest) => NTStatus.STATUS_SUCCESS;

    public NTStatus LockFile(object handle, long byteOffset, long length, bool exclusiveLock)
        => NTStatus.STATUS_SUCCESS;

    public NTStatus UnlockFile(object handle, long byteOffset, long length)
        => NTStatus.STATUS_SUCCESS;

    public NTStatus NotifyChange(
        out object ioRequest, object handle,
        NotifyChangeFilter completionFilter, bool watchTree, int outputBufferSize,
        OnNotifyChangeCompleted onNotifyChangeCompleted, object context)
    { ioRequest = null!; return NTStatus.STATUS_NOT_SUPPORTED; }

    public NTStatus SetFileSystemInformation(FileSystemInformation information)
        => NTStatus.STATUS_NOT_SUPPORTED;

    // ═══════════════════════ HELPERS ═══════════════════════

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

    // ------------------ FileInformation builders ------------------

    private static FileInformation BuildSnapshotFileInfo(
        long size, DateTime timestamp, bool isDirectory, FileInformationClass cls)
    {
        var attrs = isDirectory
            ? FileAttributes.Directory | FileAttributes.ReadOnly
            : FileAttributes.Normal | FileAttributes.ReadOnly;

        long alloc = RoundUpAllocation(size);

        return cls switch
        {
            FileInformationClass.FileBasicInformation => new FileBasicInformation
            {
                CreationTime = timestamp,
                LastWriteTime = timestamp,
                LastAccessTime = timestamp,
                ChangeTime = timestamp,
                FileAttributes = attrs
            },
            FileInformationClass.FileStandardInformation => new FileStandardInformation
            {
                AllocationSize = alloc,
                EndOfFile = size,
                NumberOfLinks = 1,
                DeletePending = false,
                Directory = isDirectory
            },
            FileInformationClass.FileNetworkOpenInformation => new FileNetworkOpenInformation
            {
                CreationTime = timestamp,
                LastWriteTime = timestamp,
                LastAccessTime = timestamp,
                ChangeTime = timestamp,
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
            FileInformationClass.FileStreamInformation when !isDirectory =>
                CreateFileStreamInfo(size, alloc),
            _ => new FileBasicInformation
            {
                CreationTime = timestamp,
                LastWriteTime = timestamp,
                LastAccessTime = timestamp,
                ChangeTime = timestamp,
                FileAttributes = attrs
            }
        };
    }

    private static FileInformation BuildDirectoryInfo(
        DirectoryInfo d, bool deletePending, FileInformationClass cls) => cls switch
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
            FileInformationClass.FileAttributeTagInformation =>
                new FileAttributeTagInformation { FileAttributes = FileAttributes.Directory, ReparsePointTag = 0 },
            _ => new FileBasicInformation
            {
                CreationTime = d.CreationTimeUtc,
                LastWriteTime = d.LastWriteTimeUtc,
                LastAccessTime = d.LastAccessTimeUtc,
                ChangeTime = d.LastWriteTimeUtc,
                FileAttributes = FileAttributes.Directory
            }
        };

    private static FileInformation BuildFileInfo(
        FileInfo f, bool deletePending, FileInformationClass cls)
    {
        long size = f.Length;
        long alloc = RoundUpAllocation(size);
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
                AllocationSize = alloc,
                EndOfFile = size,
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
                AllocationSize = alloc,
                EndOfFile = size,
                FileAttributes = FileAttributes.Normal
            },
            FileInformationClass.FileAttributeTagInformation =>
                new FileAttributeTagInformation { FileAttributes = FileAttributes.Normal, ReparsePointTag = 0 },
            FileInformationClass.FileStreamInformation => CreateFileStreamInfo(size, alloc),
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

    // ------------------ Directory entry builders ------------------

    private static QueryDirectoryFileInformation CreateDirEntryInfo(
        string name, DirectoryInfo dirInfo, FileInformationClass cls) => cls switch
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
            FileInformationClass.FileBothDirectoryInformation => new FileBothDirectoryInformation
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
            FileInformationClass.FileIdBothDirectoryInformation => new FileIdBothDirectoryInformation
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
            FileInformationClass.FileNamesInformation => new FileNamesInformation { FileName = name },
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

    private static QueryDirectoryFileInformation CreateEntryInfo(
        string name, FileSystemInfo info, bool isDirectory, FileInformationClass cls)
    {
        var attrs = isDirectory ? FileAttributes.Directory : FileAttributes.Normal;
        long size = isDirectory ? 0 : ((FileInfo)info).Length;
        long alloc = RoundUpAllocation(size);
        string shortName = GenerateShortName(name);

        return cls switch
        {
            FileInformationClass.FileDirectoryInformation => new FileDirectoryInformation
            {
                FileName = name,
                CreationTime = info.CreationTimeUtc,
                LastAccessTime = info.LastAccessTimeUtc,
                LastWriteTime = info.LastWriteTimeUtc,
                ChangeTime = info.LastWriteTimeUtc,
                EndOfFile = size,
                AllocationSize = alloc,
                FileAttributes = attrs
            },
            FileInformationClass.FileBothDirectoryInformation => new FileBothDirectoryInformation
            {
                FileName = name,
                ShortName = shortName,
                CreationTime = info.CreationTimeUtc,
                LastAccessTime = info.LastAccessTimeUtc,
                LastWriteTime = info.LastWriteTimeUtc,
                ChangeTime = info.LastWriteTimeUtc,
                EndOfFile = size,
                AllocationSize = alloc,
                FileAttributes = attrs,
                EaSize = 0
            },
            FileInformationClass.FileIdBothDirectoryInformation => new FileIdBothDirectoryInformation
            {
                FileName = name,
                ShortName = shortName,
                CreationTime = info.CreationTimeUtc,
                LastAccessTime = info.LastAccessTimeUtc,
                LastWriteTime = info.LastWriteTimeUtc,
                ChangeTime = info.LastWriteTimeUtc,
                EndOfFile = size,
                AllocationSize = alloc,
                FileAttributes = attrs,
                EaSize = 0,
                FileId = 0
            },
            FileInformationClass.FileNamesInformation => new FileNamesInformation { FileName = name },
            _ => new FileDirectoryInformation
            {
                FileName = name,
                CreationTime = info.CreationTimeUtc,
                LastAccessTime = info.LastAccessTimeUtc,
                LastWriteTime = info.LastWriteTimeUtc,
                ChangeTime = info.LastWriteTimeUtc,
                EndOfFile = size,
                AllocationSize = alloc,
                FileAttributes = attrs
            }
        };
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

    private static long RoundUpAllocation(long size)
    {
        const long clusterSize = 4096;
        return size == 0 ? 0 : ((size + clusterSize - 1) / clusterSize) * clusterSize;
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
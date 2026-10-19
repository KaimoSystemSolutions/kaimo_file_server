using Kaimo_File_Server.Core.Domain;
using Kaimo_File_Server.Core.Helpers;
using Kaimo_File_Server.Core.Logging;
using Kaimo_File_Server.Core.Security;
using Kaimo_File_Server.Core.Storage;
using Kaimo_File_Server.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Kaimo_File_Server.Infrastructure.Storage;

public partial class FileSystemStorage : IStorageEngine
{
    private readonly string _rootPath;
    private readonly string _rootFullPath;
    private readonly Guid _shareId;
    private readonly IServiceProvider? _serviceProvider;
    private readonly ILogger _logger;

    /// <summary>
    /// Creates a storage engine bound to a specific share.
    /// </summary>
    public FileSystemStorage(string rootPath, Guid shareId, IServiceProvider? serviceProvider)
    {
        _rootPath = rootPath;
        _rootFullPath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(rootPath));
        _shareId = shareId;
        _serviceProvider = serviceProvider;
        _logger = serviceProvider?.GetService<ILogger<FileSystemStorage>>()
            ?? (ILogger)NullLogger<FileSystemStorage>.Instance;
        Directory.CreateDirectory(_rootPath);
    }

    // ------------------ Path Resolution ------------------

    public string ToAbsolutePath(string shareRelativePath)
    {
        // Internal server-owned paths (for example immutable Samba close
        // captures) are valid storage paths. Client-facing bridge ingress
        // rejects those namespaces before invoking the storage layer.
        var fullPath = ShareRelativePath.ToContainedAbsolutePath(
            _rootPath, shareRelativePath,
            allowRoot: true, allowInternalNamespace: true);
        EnsureNotThroughLink(fullPath, shareRelativePath);
        return fullPath;
    }

    /// <summary>
    /// Symbolic links below the share root (no transport can create one; only a direct write on
    /// the volume can) are never followed: a link to /etc/passwd would be served, a link to a
    /// foreign directory written into, and a loop (x -> .) recursed forever. An item reached
    /// through a link therefore counts as missing. One lstat per path segment.
    /// </summary>
    private void EnsureNotThroughLink(string fullPath, string shareRelativePath)
    {
        var root = _rootFullPath;
        for (var path = fullPath; path.Length > root.Length; path = Path.GetDirectoryName(path)!)
        {
            if (new FileInfo(path).LinkTarget is not null)
                throw new FileNotFoundException(
                    $"'{shareRelativePath}' is reached through a symbolic link, which is not followed.",
                    shareRelativePath);
        }
    }

    // Every entry except symbolic links (hidden ones included, like Directory.GetFiles).
    private static readonly EnumerationOptions WithoutLinks = new()
    {
        AttributesToSkip = FileAttributes.ReparsePoint,
        IgnoreInaccessible = false,
    };

    private static readonly EnumerationOptions WithoutLinksRecursive = new()
    {
        AttributesToSkip = FileAttributes.ReparsePoint,
        IgnoreInaccessible = false,
        RecurseSubdirectories = true,
    };

    // ------------------ Read / Write / Delete ------------------

    public Task<Stream> ReadAsync(string path)
    {
        var fullPath = ToAbsolutePath(path);
        Stream stream = new FileStream(
            fullPath, FileMode.Open, FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete, 4096, true);
        return Task.FromResult(stream);
    }

    public Task<Stream> ReadAsync(string path, bool preserveAccessTime)
    {
        if (!preserveAccessTime)
            return ReadAsync(path);

        var fullPath = ToAbsolutePath(path);
        // Captured before the open: on Linux (relatime) the first read after a write moves atime.
        var accessed = File.GetLastAccessTimeUtc(fullPath);
        Stream stream = new AccessTimePreservingFileStream(fullPath, accessed);
        return Task.FromResult(stream);
    }

    public async Task WriteAsync(string path, Stream data, CancellationToken cancellationToken = default)
    {
        var fullPath = ToAbsolutePath(path);
        var parentPath = Path.GetDirectoryName(fullPath)!;
        Directory.CreateDirectory(parentPath);

        // Publish only a completely persisted file. In particular, an interrupted
        // web overwrite must never truncate or delete the previous destination.
        var tempPath = Path.Combine(
            parentPath,
            // The name is not embedded: a long target name plus the suffix would exceed the
            // 255-byte name limit although the target name itself fits.
            $".kaimo-upload-{Guid.NewGuid():N}.tmp");

        try
        {
            await using (var file = new FileStream(
                tempPath, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                81920, FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                await data.CopyToAsync(file, cancellationToken);
                await file.FlushAsync(cancellationToken);
                file.Flush(flushToDisk: true);
            }

            cancellationToken.ThrowIfCancellationRequested();
            if (File.Exists(fullPath))
            {
                // An overwrite changes the content, not the identity: keep the creation time.
                FileBirthTime.PreserveOnReplace(fullPath, tempPath);
                File.Replace(tempPath, fullPath, destinationBackupFileName: null);
            }
            else
                File.Move(tempPath, fullPath);
        }
        finally
        {
            // No-op after the successful move; removes only unpublished data after
            // cancellation/failure and never touches the requested destination.
            try { File.Delete(tempPath); }
            catch { /* preserve the original write exception */ }
        }
    }

    public Task SetModifiedDateAsync(string path, DateTime time)
    {
        var fullTargetPath = ToAbsolutePath(path);

        var utc = time.ToUniversalTime();

        if (Directory.Exists(fullTargetPath))
        {
            FileBirthTime.PinBeforeTimeChange(new DirectoryInfo(fullTargetPath));
            Directory.SetLastWriteTimeUtc(fullTargetPath, utc);
            Directory.SetLastAccessTimeUtc(fullTargetPath, utc);
            return Task.CompletedTask;
        }

        if (!File.Exists(fullTargetPath))
            throw new IOException($"File does not exist: '{path}'");

        FileBirthTime.PinBeforeTimeChange(new FileInfo(fullTargetPath));
        File.SetLastWriteTimeUtc(fullTargetPath, utc);
        File.SetLastAccessTimeUtc(fullTargetPath, utc);
        return Task.CompletedTask;
    }

    public Task SetAccessedDateAsync(string path, DateTime time)
    {
        var fullTargetPath = ToAbsolutePath(path);
        var utc = time.ToUniversalTime();

        if (Directory.Exists(fullTargetPath))
        {
            FileBirthTime.PinBeforeTimeChange(new DirectoryInfo(fullTargetPath));
            Directory.SetLastAccessTimeUtc(fullTargetPath, utc);
            return Task.CompletedTask;
        }

        if (!File.Exists(fullTargetPath))
            throw new IOException($"File does not exist: '{path}'");

        FileBirthTime.PinBeforeTimeChange(new FileInfo(fullTargetPath));
        File.SetLastAccessTimeUtc(fullTargetPath, utc);
        return Task.CompletedTask;
    }

    public Task SetCreatedDateAsync(string path, DateTime time)
    {
        var fullTargetPath = ToAbsolutePath(path);
        FileSystemInfo info = Directory.Exists(fullTargetPath)
            ? new DirectoryInfo(fullTargetPath)
            : File.Exists(fullTargetPath)
                ? new FileInfo(fullTargetPath)
                : throw new IOException($"File does not exist: '{path}'");

        if (!FileBirthTime.TrySetUtc(info, time))
            _logger.LogWarning(LogEvents.StorageCreatedTimeNotStored, LogMessages.StorageCreatedTimeNotStored, path);
        return Task.CompletedTask;
    }
    
    public Task CreateDirectory(string dirPath)
    {
        var fullPath = ToAbsolutePath(dirPath);   
         Directory.CreateDirectory(fullPath);
        return Task.CompletedTask;
    }

    public Task RenameFileAsync(string oldPath, string newPath)
    {
        var oldFullPath = ToAbsolutePath(oldPath);
        var newFullPath = ToAbsolutePath(newPath);
        File.Move(oldFullPath, newFullPath, false);
        return Task.CompletedTask;
    }

    
    public Task RenameDirectoryAsync(string oldDirPath, string newDirPath)
    {
        var oldFullPath = ToAbsolutePath(oldDirPath);
        var newFullPath = ToAbsolutePath(newDirPath);
        Directory.Move(oldFullPath, newFullPath);
        return Task.CompletedTask;
    }

    public Task DeleteAsync(string path)
    {
        var fullPath = ToAbsolutePath(path);
        if (File.Exists(fullPath))
            File.Delete(fullPath);
        else if (Directory.Exists(fullPath))
            Directory.Delete(fullPath, true);
        return Task.CompletedTask;
    }

    public Task<bool> IsDirectoryAsync(string path)
    {
        var normalized = ShareRelativePath.Normalize(path);
        if (string.IsNullOrEmpty(normalized))
            return Task.FromResult(true);

        var fullPath = ToAbsolutePath(normalized);
        return Task.FromResult(Directory.Exists(fullPath));
    }

    public Task<string> MoveAsync(string oldPath, string newPath)
    {
        var normalizedOld = ShareRelativePath.Normalize(oldPath);
        var normalizedNew = ShareRelativePath.Normalize(newPath);

        var fullOldPath = ToAbsolutePath(normalizedOld);
        var fullNewPath = ToAbsolutePath(normalizedNew);

        Directory.CreateDirectory(Path.GetDirectoryName(fullNewPath)!);

        // On a name collision, disambiguate with a timestamp suffix. Apply it to
        // BOTH the relative and absolute path so the returned value matches what
        // actually landed on disk.
        if (File.Exists(fullNewPath) || Directory.Exists(fullNewPath))
        {
            var suffix = "_" + DateTime.UtcNow.ToString("yyyy-MM-dd_HH-mm-ss");
            normalizedNew += suffix;
            fullNewPath += suffix;
        }

        if (File.Exists(fullOldPath))
            File.Move(fullOldPath, fullNewPath);
        else if (Directory.Exists(fullOldPath))
            Directory.Move(fullOldPath, fullNewPath);

        return Task.FromResult(normalizedNew);
    }

    // ------------------ Directory Size ------------------

    public Task<long> GetDirectorySizeAsync(string relativePath)
    {
        var fullPath = ToAbsolutePath(relativePath);
        var dirInfo = new DirectoryInfo(fullPath);
        if (!dirInfo.Exists) return Task.FromResult(0L);

        var size = CalculateDirectorySizeSafe(fullPath);
        return Task.FromResult(size);
    }

    private static long CalculateDirectorySizeSafe(string path)
    {
        long total = 0;

        try
        {
            foreach (var file in Directory.EnumerateFiles(path, "*", WithoutLinks))
            {
                try { total += new FileInfo(file).Length; }
                catch { /* skip inaccessible file */ }
            }

            foreach (var dir in Directory.EnumerateDirectories(path, "*", WithoutLinks))
            {
                total += CalculateDirectorySizeSafe(dir);
            }
        }
        catch { /* skip inaccessible directory */ }

        return total;
    }

    // ------------------ Metadata ------------------

    public async Task<FileMetadata> GetMetadataAsync(string path)
    {
        var normalized = ShareRelativePath.Normalize(path);
        var fullPath = ToAbsolutePath(normalized);
        var isDir = Directory.Exists(fullPath);

        // A missing item is reported, never synthesized: callers rely on this as their
        // existence probe (404s, create-only uploads, sync parent checks).
        if (!isDir && !File.Exists(fullPath))
            throw new FileNotFoundException($"'{normalized}' does not exist.", normalized);

        List<AccessEntry> acl = new();

        if (_serviceProvider != null)
        {
            try
            {
                using var scope = _serviceProvider.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
                var dbMeta = await db.FileMetadata
                    .Include(m => m.Acl)
                    .FirstOrDefaultAsync(m => m.ShareId == _shareId && m.Path == normalized);
                if (dbMeta?.Acl != null)
                    acl = dbMeta.Acl;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(LogEvents.StorageAclLoadFailed, ex, LogMessages.StorageAclLoadFailed, normalized);
            }
        }

        if (isDir)
        {
            var dirInfo = new DirectoryInfo(fullPath);
            return new FileMetadata
            {
                ShareId = _shareId,
                Path = normalized,
                Name = ShareRelativePath.GetFileName(normalized),
                Size = 0,
                IsDirectory = true,
                CreatedAt = FileBirthTime.GetUtc(dirInfo),
                ModifiedAt = dirInfo.LastWriteTimeUtc,
                LastAccessedAt = dirInfo.LastAccessTimeUtc,
                Acl = acl
            };
        }
        else
        {
            var fileInfo = new FileInfo(fullPath);
            return new FileMetadata
            {
                ShareId = _shareId,
                Path = normalized,
                Name = ShareRelativePath.GetFileName(normalized),
                Size = fileInfo.Length,
                IsDirectory = false,
                CreatedAt = FileBirthTime.GetUtc(fileInfo),
                ModifiedAt = fileInfo.LastWriteTimeUtc,
                LastAccessedAt = fileInfo.LastAccessTimeUtc,
                Acl = acl
            };
        }
    }

    // ------------------ Directory Listing ------------------

    public Task<List<FileMetadata>> ListAsync(string directoryPath)
    {
        var normalized = ShareRelativePath.Normalize(directoryPath);
        var fullPath = string.IsNullOrEmpty(normalized)
            ? _rootPath
            : ToAbsolutePath(normalized);

        var entries = new List<FileMetadata>();

        if (!Directory.Exists(fullPath))
            throw new DirectoryNotFoundException($"Directory '{normalized}' does not exist.");

        foreach (var dir in Directory.GetDirectories(fullPath, "*", WithoutLinks))
        {
            var dirInfo = new DirectoryInfo(dir);
            entries.Add(new FileMetadata
            {
                Id = Guid.Empty,
                ShareId = _shareId,
                Path = ShareRelativePath.Combine(normalized, dirInfo.Name),
                Name = dirInfo.Name,
                Size = 0,
                IsDirectory = true,
                CreatedAt = FileBirthTime.GetUtc(dirInfo),
                ModifiedAt = dirInfo.LastWriteTimeUtc,
                LastAccessedAt = dirInfo.LastAccessTimeUtc
            });
        }

        foreach (var file in Directory.GetFiles(fullPath, "*", WithoutLinks))
        {
            var fileInfo = new FileInfo(file);
            entries.Add(new FileMetadata
            {
                Id = Guid.Empty,
                ShareId = _shareId,
                Path = ShareRelativePath.Combine(normalized, fileInfo.Name),
                Name = fileInfo.Name,
                Size = fileInfo.Length,
                IsDirectory = false,
                CreatedAt = FileBirthTime.GetUtc(fileInfo),
                ModifiedAt = fileInfo.LastWriteTimeUtc,
                LastAccessedAt = fileInfo.LastAccessTimeUtc
            });
        }

        return Task.FromResult(entries);
    }

    // ------------------ Directory Creation ------------------

    public Task CreateDirectoryAsync(string path)
    {
        var fullPath = ToAbsolutePath(path);
        Directory.CreateDirectory(fullPath);
        return Task.CompletedTask;
    }

    public Task<bool> ExistsAsync(string path)
    {
        var full = ToAbsolutePath(ShareRelativePath.Normalize(path));
        return Task.FromResult(File.Exists(full) || Directory.Exists(full));
    }

    public Task<IStorageHandle> OpenAsync(
        string path, OpenMode mode, AccessIntent intent, ShareIntent share,
        CancellationToken ct = default)
    {
        var normalized = ShareRelativePath.Normalize(path);
        var fullPath = ToAbsolutePath(normalized);

        // Directory: no FileStream needed
        if (Directory.Exists(fullPath))
        {
            return Task.FromResult<IStorageHandle>(
                new FileSystemStorageHandle(null, fullPath, normalized, isDirectory: true));
        }

        // Ensure parent exists for create modes
        if (mode is OpenMode.Create or OpenMode.OpenOrCreate
                 or OpenMode.CreateOrTruncate or OpenMode.Supersede)
        {
            var parent = Path.GetDirectoryName(fullPath);
            if (parent != null) Directory.CreateDirectory(parent);
        }

        var fileMode = mode switch
        {
            OpenMode.Open => FileMode.Open,
            OpenMode.OpenOrCreate => FileMode.OpenOrCreate,
            OpenMode.Create => FileMode.CreateNew,
            OpenMode.Truncate => FileMode.Truncate,
            OpenMode.CreateOrTruncate => FileMode.Create,
            OpenMode.Supersede => FileMode.Create,
            _ => FileMode.OpenOrCreate
        };

        // We always open ReadWrite for write intents so close-time hooks
        // (versioning) can read the same stream back without re-opening.
        var fileAccess = intent == AccessIntent.Read
            ? FileAccess.Read
            : FileAccess.ReadWrite;

        var fileShare = FileShare.None;
        if ((share & ShareIntent.Read) != 0) fileShare |= FileShare.Read;
        if ((share & ShareIntent.Write) != 0) fileShare |= FileShare.Write;
        if ((share & ShareIntent.Delete) != 0) fileShare |= FileShare.Delete;

        var fs = new FileStream(fullPath, fileMode, fileAccess, fileShare,
                                bufferSize: 4096, useAsync: true);

        return Task.FromResult<IStorageHandle>(
            new FileSystemStorageHandle(fs, fullPath, normalized, isDirectory: false,
                                        fileAccess, fileShare));
    }
}

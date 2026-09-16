using System.IO.Compression;
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
using System.Formats.Tar;

namespace Kaimo_File_Server.Infrastructure.Storage;

public class FileSystemStorage : IStorageEngine
{
    private sealed class FileSystemStorageHandle : IStorageHandle
    {
        private FileStream? _stream;
        private bool _disposed;

        // Remembered so the stream can be reopened identically after a move.
        private readonly FileAccess _access;
        private readonly FileShare _share;

        public string RelativePath { get; private set; }
        public string AbsolutePath { get; private set; }
        public bool IsDirectory { get; }
        public long Length => _stream?.Length ?? 0;
        public bool IsDirty { get; private set; }
        public bool DeleteOnClose { get; private set; }

        public FileSystemStorageHandle(
            FileStream? stream, string absolutePath, string relativePath, bool isDirectory,
            FileAccess access = FileAccess.Read, FileShare share = FileShare.None)
        {
            _stream = stream;
            AbsolutePath = absolutePath;
            RelativePath = relativePath;
            IsDirectory = isDirectory;
            _access = access;
            _share = share;
        }

        public async ValueTask MoveAsync(
            string newAbsolutePath, string newRelativePath,
            bool replaceExisting, CancellationToken ct = default)
        {
            if (_disposed) throw new ObjectDisposedException(nameof(FileSystemStorageHandle));

            if (string.Equals(AbsolutePath, newAbsolutePath, StringComparison.Ordinal))
                return;

            if (IsDirectory)
            {
                if (Directory.Exists(newAbsolutePath) || File.Exists(newAbsolutePath))
                {
                    if (!replaceExisting)
                        throw new IOException($"Target already exists: '{newAbsolutePath}'.");
                    if (Directory.Exists(newAbsolutePath)) Directory.Delete(newAbsolutePath, true);
                    else File.Delete(newAbsolutePath);
                }

                Directory.CreateDirectory(Path.GetDirectoryName(newAbsolutePath)!);
                Directory.Move(AbsolutePath, newAbsolutePath);
                AbsolutePath = newAbsolutePath;
                RelativePath = newRelativePath;
                return;
            }

            // File: close → move → reopen so the handle stays usable.
            long position = _stream?.Position ?? 0;
            if (_stream != null) await _stream.DisposeAsync();

            try
            {
                if (File.Exists(newAbsolutePath) || Directory.Exists(newAbsolutePath))
                {
                    if (!replaceExisting)
                        throw new IOException($"Target already exists: '{newAbsolutePath}'.");
                    if (File.Exists(newAbsolutePath)) File.Delete(newAbsolutePath);
                    else Directory.Delete(newAbsolutePath, true);
                }

                Directory.CreateDirectory(Path.GetDirectoryName(newAbsolutePath)!);
                File.Move(AbsolutePath, newAbsolutePath);
                AbsolutePath = newAbsolutePath;
                RelativePath = newRelativePath;
            }
            catch
            {
                // Reopen the source so the caller's handle remains usable on failure.
                if (File.Exists(AbsolutePath))
                {
                    _stream = new FileStream(AbsolutePath, FileMode.Open, _access, _share,
                                             bufferSize: 4096, useAsync: true);
                    if (position > 0) _stream.Position = Math.Min(position, _stream.Length);
                }
                throw;
            }

            _stream = new FileStream(AbsolutePath, FileMode.Open, _access, _share,
                                     bufferSize: 4096, useAsync: true);
            if (position > 0) _stream.Position = Math.Min(position, _stream.Length);
        }

        public async ValueTask<int> ReadAsync(long offset, Memory<byte> buffer, CancellationToken ct)
        {
            if (_stream == null) throw new InvalidOperationException("Directory handle is not readable.");
            if (_stream.Position != offset) _stream.Position = offset;
            return await _stream.ReadAsync(buffer, ct);
        }

        public async ValueTask WriteAsync(long offset, ReadOnlyMemory<byte> data, CancellationToken ct)
        {
            if (_stream == null) throw new InvalidOperationException("Directory handle is not writable.");
            if (_stream.Position != offset) _stream.Position = offset;
            await _stream.WriteAsync(data, ct);
            IsDirty = true;
        }

        public ValueTask SetLengthAsync(long length, CancellationToken ct)
        {
            if (_stream == null) throw new InvalidOperationException();
            _stream.SetLength(length);
            IsDirty = true;
            return ValueTask.CompletedTask;
        }

        public ValueTask SetTimesAsync(FileTimes times, CancellationToken ct)
        {
            if (IsDirectory)
            {
                var d = new DirectoryInfo(AbsolutePath);
                if (times.Created is { } c && c > DateTime.MinValue) d.CreationTimeUtc = c;
                if (times.LastWritten is { } w && w > DateTime.MinValue) d.LastWriteTimeUtc = w;
                if (times.LastAccessed is { } a && a > DateTime.MinValue) d.LastAccessTimeUtc = a;
            }
            else
            {
                var f = new FileInfo(AbsolutePath);
                if (times.Created is { } c && c > DateTime.MinValue) f.CreationTimeUtc = c;
                if (times.LastWritten is { } w && w > DateTime.MinValue) f.LastWriteTimeUtc = w;
                if (times.LastAccessed is { } a && a > DateTime.MinValue) f.LastAccessTimeUtc = a;
            }
            return ValueTask.CompletedTask;
        }

        public async ValueTask FlushAsync(CancellationToken ct)
        {
            if (_stream != null) await _stream.FlushAsync(ct);
        }

        public Stream? GetReadableSnapshot()
        {
            if (_stream == null || !_stream.CanRead) return null;
            _stream.Flush(true);
            _stream.Position = 0;
            return _stream;
        }

        public void MarkDeleteOnClose() => DeleteOnClose = true;

        public async ValueTask DisposeAsync()
        {
            if (_disposed) return;
            _disposed = true;

            if (_stream != null)
                await _stream.DisposeAsync();

            if (DeleteOnClose)
            {
                try
                {
                    if (IsDirectory && Directory.Exists(AbsolutePath))
                        Directory.Delete(AbsolutePath, recursive: true);
                    else if (!IsDirectory && File.Exists(AbsolutePath))
                        File.Delete(AbsolutePath);
                }
                catch { /* permission was validated at Open */ }
            }
        }
    }


    private readonly string _rootPath;
    private readonly Guid _shareId;
    private readonly IServiceProvider? _serviceProvider;
    private readonly ILogger _logger;

    /// <summary>
    /// Creates a storage engine bound to a specific share.
    /// </summary>
    public FileSystemStorage(string rootPath, Guid shareId, IServiceProvider? serviceProvider)
    {
        _rootPath = rootPath;
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
        return ShareRelativePath.ToContainedAbsolutePath(
            _rootPath, shareRelativePath,
            allowRoot: true, allowInternalNamespace: true);
    }

    // ------------------ Read / Write / Delete ------------------

    public Task<Stream> ReadAsync(string path)
    {
        var fullPath = ToAbsolutePath(path);
        Stream stream = new FileStream(
            fullPath, FileMode.Open, FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete, 4096, true);
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
            $".{Path.GetFileName(fullPath)}.kaimo-{Guid.NewGuid():N}.tmp");

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
                File.Replace(tempPath, fullPath, destinationBackupFileName: null);
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

        if (!File.Exists(fullTargetPath))
            throw new IOException($"File does not exist: '{path}'");
        
        File.SetLastWriteTimeUtc(fullTargetPath, time.ToUniversalTime());
        return Task.CompletedTask;
    }
    

    public async Task ArchiveAsync(List<string> sourcePaths, string targetPath, string format)
    {
        var fullTargetPath = ToAbsolutePath(targetPath);

        if (File.Exists(fullTargetPath))
            throw new IOException($"File already exists: '{targetPath}'");

        await Task.Run(() =>
        {
            switch (format)
            {
                case ".zip":
                    CreateZip(sourcePaths, fullTargetPath);
                    break;
                case ".tar.gz":
                    CreateTarGz(sourcePaths, fullTargetPath);
                    break;
                default:
                    throw new NotSupportedException($"Archive format '{format}' is not supported.");
            }
        });
    }


    private void CreateZip(List<string> sourcePaths, string targetPath)
    {
        using var zip = ZipFile.Open(targetPath, ZipArchiveMode.Create);
    
        foreach (var sourcePath in sourcePaths)
        {
            var fullPath = ToAbsolutePath(sourcePath);
    
            if (Directory.Exists(fullPath))
                AddDirectoryToZip(zip, fullPath, Path.GetFileName(fullPath));
            else if (File.Exists(fullPath))
                zip.CreateEntryFromFile(fullPath, Path.GetFileName(fullPath));
        }
    }
    
    private void AddDirectoryToZip(ZipArchive zip, string dirPath, string entryBase)
    {
        // Do NOT use SearchOption.AllDirectories: it follows symlinks, so a link inside
        // the share would pull its target's bytes — possibly outside the share root —
        // into a user-downloadable archive. SafeDirectoryWalk never follows links.
        var result = SafeDirectoryWalk.EnumerateFiles(dirPath, walked =>
        {
            var entryName = Path.Combine(entryBase, Path.GetRelativePath(dirPath, walked.FullPath))
                .Replace('\\', '/');
            zip.CreateEntryFromFile(walked.FullPath, entryName);
        });
        LogWalkSkips(result, dirPath);
    }
    
    private void CreateTarGz(List<string> sourcePaths, string targetPath)
    {
        using var fileStream = File.Create(targetPath);
        using var gzipStream = new GZipStream(fileStream, CompressionLevel.Optimal);
        using var tarWriter = new TarWriter(gzipStream);
    
        foreach (var sourcePath in sourcePaths)
        {
            var fullPath = ToAbsolutePath(sourcePath);
    
            if (Directory.Exists(fullPath))
                AddDirectoryToTar(tarWriter, fullPath, Path.GetFileName(fullPath));
            else if (File.Exists(fullPath))
                tarWriter.WriteEntry(fullPath, Path.GetFileName(fullPath));
        }
    }
    
    private void AddDirectoryToTar(TarWriter tarWriter, string dirPath, string entryBase)
    {
        // See AddDirectoryToZip: SearchOption.AllDirectories follows symlinks and would
        // exfiltrate a link target's bytes into the archive. SafeDirectoryWalk does not.
        var result = SafeDirectoryWalk.EnumerateFiles(dirPath, walked =>
        {
            var entryName = Path.Combine(entryBase, Path.GetRelativePath(dirPath, walked.FullPath))
                .Replace('\\', '/');
            tarWriter.WriteEntry(walked.FullPath, entryName);
        });
        LogWalkSkips(result, dirPath);
    }

    public async Task UnzipAsync(string zipPath, string targetPath)
    {
        var fullZipPath = ToAbsolutePath(zipPath);
        var fullTargetPath = ToAbsolutePath(targetPath);

        if (Directory.Exists(fullTargetPath))
            throw new IOException($"Directory already exists: '{targetPath}'");

        await Task.Run(() =>
        {
            ZipFile.ExtractToDirectory(fullZipPath, fullTargetPath);
        });
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

    public Task DeleteAsync(string path, bool ignoreMissing = false)
    {
        var fullPath = ToAbsolutePath(path);
        if (File.Exists(fullPath))
            File.Delete(fullPath);
        else if (Directory.Exists(fullPath))
            Directory.Delete(fullPath, true);
        else if (!ignoreMissing)
            throw new FileNotFoundException(
                $"Cannot delete '{path}': no file or directory exists at that path.", fullPath);
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
        else
            throw new FileNotFoundException(
                $"Cannot move '{oldPath}': no file or directory exists at the source path.", fullOldPath);

        return Task.FromResult(normalizedNew);
    }

    // ------------------ Directory Size ------------------

    public Task<long> GetDirectorySizeAsync(string relativePath, CancellationToken cancellationToken = default)
    {
        var fullPath = ToAbsolutePath(relativePath);
        if (!Directory.Exists(fullPath)) return Task.FromResult(0L);

        // The walk is synchronous syscalls; running it on a thread-pool thread is the
        // honest representation and lets a re-navigation cancel a walk already in flight.
        return Task.Run(() =>
        {
            long total = 0;
            var result = SafeDirectoryWalk.EnumerateFiles(
                fullPath, f => total += f.Length, cancellationToken: cancellationToken);
            LogWalkSkips(result, fullPath);
            return total;
        }, cancellationToken);
    }

    /// <summary>
    /// Emits a single Debug line when a walk skipped anything (symlinks, depth cap,
    /// entry cap or inaccessible entries). Debug — not Warning — because a share with a
    /// couple of symlinks would otherwise log on every navigation. Content reachable
    /// only through a skipped symlink is intentionally excluded from the result.
    /// </summary>
    private void LogWalkSkips(WalkResult result, string rootFullPath)
    {
        if (result.Skips.Count == 0) return;
        _logger.LogDebug(
            LogEvents.StorageWalkTruncated, LogMessages.StorageWalkTruncated,
            rootFullPath, result.Skips.Count);
    }

    // ------------------ Metadata ------------------

    public async Task<FileMetadata> GetMetadataAsync(string path)
    {
        var normalized = ShareRelativePath.Normalize(path);
        var fullPath = ToAbsolutePath(normalized);
        var isDir = Directory.Exists(fullPath);

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
                CreatedAt = dirInfo.CreationTimeUtc,
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
                Size = fileInfo.Exists ? fileInfo.Length : 0,
                IsDirectory = false,
                CreatedAt = fileInfo.Exists ? fileInfo.CreationTimeUtc : DateTime.UtcNow,
                ModifiedAt = fileInfo.Exists ? fileInfo.LastWriteTimeUtc : DateTime.UtcNow,
                LastAccessedAt = fileInfo.Exists ? fileInfo.LastAccessTimeUtc : null,
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

        foreach (var dir in Directory.GetDirectories(fullPath))
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
                CreatedAt = dirInfo.CreationTimeUtc,
                ModifiedAt = dirInfo.LastWriteTimeUtc,
                LastAccessedAt = dirInfo.LastAccessTimeUtc
            });
        }

        foreach (var file in Directory.GetFiles(fullPath))
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
                CreatedAt = fileInfo.CreationTimeUtc,
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

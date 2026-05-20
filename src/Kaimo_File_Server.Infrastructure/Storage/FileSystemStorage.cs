using Kaimo_File_Server.Core.Domain;
using Kaimo_File_Server.Core.Helpers;
using Kaimo_File_Server.Core.Security;
using Kaimo_File_Server.Core.Storage;
using Kaimo_File_Server.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Kaimo_File_Server.Infrastructure.Storage;

public class FileSystemStorage : IStorageEngine
{
    private readonly string _rootPath;
    private readonly Guid _shareId;
    private readonly IServiceProvider? _serviceProvider;

    public FileSystemStorage(string rootPath, Guid shareId, IServiceProvider serviceProvider)
    {
        _rootPath = rootPath;
        _shareId = shareId;
        _serviceProvider = serviceProvider;
        Directory.CreateDirectory(_rootPath);
    }

    public FileSystemStorage(string rootPath)
    {
        _rootPath = rootPath;
        _shareId = new Guid();
        _serviceProvider = null;
        Directory.CreateDirectory(_rootPath);
    }

    // ────────────────── Path Resolution ──────────────────

    /// <summary>
    /// Resolves a share-relative path to an absolute filesystem path.
    /// Includes path traversal protection.
    /// </summary>
    private string ToAbsolutePath(string shareRelativePath)
    {
        var normalized = ShareRelativePath.Normalize(shareRelativePath);

        var root = Path.GetFullPath(_rootPath)
            .TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;

        var full = string.IsNullOrEmpty(normalized)
            ? root.TrimEnd(Path.DirectorySeparatorChar)
            : Path.GetFullPath(Path.Combine(root, normalized));

        if (!full.StartsWith(root.TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase))
            throw new UnauthorizedAccessException("Path traversal detected");

        return full;
    }

    // ────────────────── Read / Write / Delete ──────────────────

    public Task<Stream> ReadAsync(string path)
    {
        var fullPath = ToAbsolutePath(path);
        Stream stream = new FileStream(
            fullPath, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, true);
        return Task.FromResult(stream);
    }

    public async Task WriteAsync(string path, Stream data)
    {
        var fullPath = ToAbsolutePath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);

        using var file = new FileStream(
            fullPath, FileMode.Create, FileAccess.Write, FileShare.ReadWrite, 4096, true);
        await data.CopyToAsync(file);
    }

    public async Task CreateDirectory(string dirPath)
    {
        var fullPath = ToAbsolutePath(dirPath);
        Directory.CreateDirectory(fullPath);
    }

    public async Task RenameFileAsync(string oldPath, string newPath)
    {
        var oldFullPath = ToAbsolutePath(oldPath);
        var newFullPath = ToAbsolutePath(newPath);

        File.Move(oldFullPath, newFullPath, false);
    }

    public async Task RenameDirectoryAsync(string oldDirPath, string newDirPath)
    {
        var oldFullPath = ToAbsolutePath(oldDirPath);
        var newFullPath = ToAbsolutePath(newDirPath);

        Directory.Move(oldFullPath, newFullPath);
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
            return Task.FromResult(true); // Share root is always a directory

        var fullPath = ToAbsolutePath(normalized);
        return Task.FromResult(Directory.Exists(fullPath));
    }

    // ────────────────── Directory Size ──────────────────

    /// <inheritdoc />
    public Task<long> GetDirectorySizeAsync(string directoryPath)
    {
        var normalized = ShareRelativePath.Normalize(directoryPath);
        var fullPath = string.IsNullOrEmpty(normalized)
            ? _rootPath
            : ToAbsolutePath(normalized);

        if (!Directory.Exists(fullPath))
            return Task.FromResult(0L);

        return Task.FromResult(CalculateDirectorySize(fullPath));
    }

    /// <summary>
    /// Recursively calculates total file size within a directory.
    /// Uses EnumerateFiles for memory efficiency on large trees.
    /// </summary>
    private static long CalculateDirectorySize(string path)
    {
        try
        {
            return new DirectoryInfo(path)
                .EnumerateFiles("*", SearchOption.AllDirectories)
                .Sum(f => f.Length);
        }
        catch (UnauthorizedAccessException)
        {
            // If we can't read some subdirectory, return what we can
            return CalculateDirectorySizeSafe(path);
        }
    }

    /// <summary>
    /// Fallback that skips inaccessible subdirectories instead of failing.
    /// </summary>
    private static long CalculateDirectorySizeSafe(string path)
    {
        long total = 0;

        try
        {
            foreach (var file in Directory.EnumerateFiles(path))
            {
                try { total += new FileInfo(file).Length; }
                catch { /* skip inaccessible file */ }
            }

            foreach (var dir in Directory.EnumerateDirectories(path))
            {
                total += CalculateDirectorySizeSafe(dir);
            }
        }
        catch { /* skip inaccessible directory */ }

        return total;
    }

    // ────────────────── Metadata ──────────────────

    public async Task<FileMetadata> GetMetadataAsync(string path)
    {
        var normalized = ShareRelativePath.Normalize(path);
        var fullPath = ToAbsolutePath(normalized);
        var isDir = Directory.Exists(fullPath);

        List<AccessEntry> acl = new();

        // Try to load ACLs from DB if a service provider is available
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
                Console.WriteLine($"[FileSystemStorage] ACL load failed for '{normalized}': {ex.Message}");
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
                Size = CalculateDirectorySize(fullPath),
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

    // ────────────────── Directory Listing ──────────────────

    public Task<List<FileMetadata>> ListAsync(string directoryPath)
    {
        var normalized = ShareRelativePath.Normalize(directoryPath);
        var fullPath = string.IsNullOrEmpty(normalized)
            ? _rootPath
            : ToAbsolutePath(normalized);

        var entries = new List<FileMetadata>();

        if (!Directory.Exists(fullPath))
            return Task.FromResult(entries);

        foreach (var dir in Directory.GetDirectories(fullPath))
        {
            var dirInfo = new DirectoryInfo(dir);
            entries.Add(new FileMetadata
            {
                Id = Guid.Empty,
                ShareId = _shareId,
                Path = ShareRelativePath.Combine(normalized, dirInfo.Name),
                Name = dirInfo.Name,
                Size = CalculateDirectorySize(dir),
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

    // ────────────────── Directory Creation ──────────────────

    public Task CreateDirectoryAsync(string path)
    {
        var fullPath = ToAbsolutePath(path);
        Directory.CreateDirectory(fullPath);
        return Task.CompletedTask;
    }
}
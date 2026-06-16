using System.IO.Compression;
using Kaimo_File_Server.Core.Domain;
using Kaimo_File_Server.Core.Helpers;
using Kaimo_File_Server.Core.Security;
using Kaimo_File_Server.Core.Storage;
using Kaimo_File_Server.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using System.Formats.Tar;

namespace Kaimo_File_Server.Infrastructure.Storage;

public class FileSystemStorage : IStorageEngine
{
    private readonly string _rootPath;
    private readonly Guid _shareId;
    private readonly IServiceProvider? _serviceProvider;

    /// <summary>
    /// Creates a storage engine bound to a specific share.
    /// </summary>
    public FileSystemStorage(string rootPath, Guid shareId, IServiceProvider? serviceProvider)
    {
        _rootPath = rootPath;
        _shareId = shareId;
        _serviceProvider = serviceProvider;
        Directory.CreateDirectory(_rootPath);
    }

    // ------------------ Path Resolution ------------------

    public string ToAbsolutePath(string shareRelativePath)
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

    // ------------------ Read / Write / Delete ------------------

    public Task<Stream> ReadAsync(string path)
    {
        var fullPath = ToAbsolutePath(path);
        Stream stream = new FileStream(
            fullPath, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, true);
        return Task.FromResult(stream);
    }

    public async Task WriteAsync(string path, Stream data, CancellationToken cancellationToken = default)
    {
        var fullPath = ToAbsolutePath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);

        await using var file = new FileStream(
            fullPath, FileMode.Create, FileAccess.Write, FileShare.ReadWrite, 4096, true);
        await data.CopyToAsync(file, cancellationToken);
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
        foreach (var file in Directory.GetFiles(dirPath, "*", SearchOption.AllDirectories))
        {
            var entryName = Path.Combine(entryBase, Path.GetRelativePath(dirPath, file))
                .Replace('\\', '/');
            zip.CreateEntryFromFile(file, entryName);
        }
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
        foreach (var file in Directory.GetFiles(dirPath, "*", SearchOption.AllDirectories))
        {
            var entryName = Path.Combine(entryBase, Path.GetRelativePath(dirPath, file))
                .Replace('\\', '/');
            tarWriter.WriteEntry(file, entryName);
        }
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

    public Task MoveAsync(string oldPath, string newPath)
    {
        var fullOldPath = ToAbsolutePath(oldPath);
        var fullNewPath = ToAbsolutePath(newPath);

        Directory.CreateDirectory(Path.GetDirectoryName(fullNewPath)!);

        if (File.Exists(fullNewPath) || Directory.Exists(fullNewPath))
            fullNewPath += "_" + DateTime.UtcNow.ToString("yyyy-MM-dd_HH-mm-ss");

        if (File.Exists(fullOldPath))
            File.Move(fullOldPath, fullNewPath);
        else if (Directory.Exists(fullOldPath))
            Directory.Move(fullOldPath, fullNewPath);

        return Task.CompletedTask;
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
}
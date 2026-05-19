using Kaimo_File_Server.Core.Domain;
using Kaimo_File_Server.Core.Security;
using Kaimo_File_Server.Core.Storage;
using Kaimo_File_Server.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Kaimo_File_Server.Infrastructure.Storage
{
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

        private string GetFullPath(string path)
        {
            var root = Path.GetFullPath(_rootPath)
                .TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            var full = Path.GetFullPath(Path.Combine(root, path));
            if (!full.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                throw new UnauthorizedAccessException("Path traversal detected");
            return full;
        }

        public Task<Stream> ReadAsync(string path)
        {
            var fullPath = GetFullPath(path);
            Stream stream = new FileStream(fullPath, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, true);
            return Task.FromResult(stream);
        }

        public async Task WriteAsync(string path, Stream data)
        {
            var fullPath = GetFullPath(path);
            Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
            using var file = new FileStream(fullPath, FileMode.Create, FileAccess.Write, FileShare.ReadWrite, 4096, true);
            await data.CopyToAsync(file);
        }

        public Task DeleteAsync(string path)
        {
            var fullPath = GetFullPath(path);
            if (File.Exists(fullPath)) File.Delete(fullPath);
            else if (Directory.Exists(fullPath)) Directory.Delete(fullPath, true);
            return Task.CompletedTask;
        }

        public Task<bool> IsDirectoryAsync(string path)
        {
            if (string.IsNullOrEmpty(path)) return Task.FromResult(true);
            var fullPath = GetFullPath(path);
            return Task.FromResult(Directory.Exists(fullPath));
        }

        public async Task<FileMetadata> GetMetadataAsync(string path)
        {
            var fullPath = GetFullPath(path);
            var info = new FileInfo(fullPath);

            IReadOnlyList<AccessEntry> acl = Array.Empty<AccessEntry>();

            if (_serviceProvider != null)
            {
                try
                {
                    using var scope = _serviceProvider.CreateScope();
                    var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
                    var dbMeta = await db.FileMetadata
                        .Include(m => m.Acl)
                        .FirstOrDefaultAsync(m => m.ShareId == _shareId && m.Path == path);
                    if (dbMeta?.Acl != null) acl = dbMeta.Acl;
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[FileSystemStorage] ACL load failed for '{path}': {ex.Message}");
                }
            }

            return new FileMetadata
            {
                ShareId = _shareId,
                Path = path,
                Name = info.Name,
                Size = info.Exists ? info.Length : 0,
                IsDirectory = Directory.Exists(fullPath),
                CreatedAt = info.Exists ? info.CreationTimeUtc : DateTime.UtcNow,
                ModifiedAt = info.Exists ? info.LastWriteTimeUtc : DateTime.UtcNow,
                Acl = acl
            };
        }

        public Task<List<FileMetadata>> ListAsync(string directoryPath)
        {
            var cleanPath = directoryPath.Trim('/');
            var fullPath = string.IsNullOrEmpty(cleanPath) ? _rootPath : GetFullPath(cleanPath);
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
                    Path = string.IsNullOrEmpty(cleanPath) ? dirInfo.Name : $"{cleanPath}/{dirInfo.Name}",
                    Name = dirInfo.Name,
                    Size = 0,
                    IsDirectory = true,
                    CreatedAt = dirInfo.CreationTimeUtc,
                    ModifiedAt = dirInfo.LastWriteTimeUtc
                });
            }

            foreach (var file in Directory.GetFiles(fullPath))
            {
                var fileInfo = new FileInfo(file);
                entries.Add(new FileMetadata
                {
                    Id = Guid.Empty,
                    ShareId = _shareId,
                    Path = string.IsNullOrEmpty(cleanPath) ? fileInfo.Name : $"{cleanPath}/{fileInfo.Name}",
                    Name = fileInfo.Name,
                    Size = fileInfo.Length,
                    IsDirectory = false,
                    CreatedAt = fileInfo.CreationTimeUtc,
                    ModifiedAt = fileInfo.LastWriteTimeUtc
                });
            }

            return Task.FromResult(entries);
        }

        public Task CreateDirectoryAsync(string path)
        {
            var fullPath = GetFullPath(path.Trim('/'));
            Directory.CreateDirectory(fullPath);
            return Task.CompletedTask;
        }
    }
}
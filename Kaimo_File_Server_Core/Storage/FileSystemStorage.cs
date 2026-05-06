using Kaimo_File_Server_Core.Core.Domain;
using Kaimo_File_Server_Core.Core.Security;
using Kaimo_File_Server_Core.Core.Storage;
using Kaimo_File_Server_Core.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Kaimo_File_Server_Core.Storage
{
    /// <summary>
    /// File system storage that also loads ACL metadata from the database.
    /// 
    /// Strategie: Die Dateien liegen auf dem Filesystem, aber die ACLs kommen aus
    /// der Datenbank. GetMetadataAsync kombiniert beides.
    /// 
    /// HINWEIS: Diese Klasse braucht jetzt Zugriff auf die DB.
    /// Da sie als Singleton registriert ist, erstellt sie Scopes selbst.
    /// Alternative: Als Scoped registrieren und direkt DbContext injizieren.
    /// </summary>
    internal class FileSystemStorage : IStorageEngine
    {
        private readonly string _rootPath;
        private readonly IServiceProvider? _serviceProvider;

        /// <summary>
        /// Konstruktor für Nutzung mit DB-Anbindung (ACLs werden geladen).
        /// </summary>
        public FileSystemStorage(string rootPath, IServiceProvider serviceProvider)
        {
            _rootPath = rootPath;
            _serviceProvider = serviceProvider;
            Directory.CreateDirectory(_rootPath);
        }

        /// <summary>
        /// Konstruktor ohne DB — ACLs bleiben leer (für Tests oder Standalone).
        /// </summary>
        public FileSystemStorage(string rootPath)
        {
            _rootPath = rootPath;
            Directory.CreateDirectory(_rootPath);
        }

        private string GetFullPath(string path)
        {
            var root = Path.GetFullPath(_rootPath)
                .TrimEnd(Path.DirectorySeparatorChar)
                + Path.DirectorySeparatorChar;

            var full = Path.GetFullPath(Path.Combine(root, path));

            if (!full.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                throw new UnauthorizedAccessException("Path traversal detected");

            return full;
        }

        public Task<Stream> ReadAsync(string path)
        {
            var fullPath = GetFullPath(path);

            Stream stream = new FileStream(
                fullPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                bufferSize: 4096,
                useAsync: true
            );

            return Task.FromResult(stream);
        }

        public async Task WriteAsync(string path, Stream data)
        {
            var fullPath = GetFullPath(path);

            Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);

            using var file = new FileStream(
                fullPath,
                FileMode.Create,
                FileAccess.Write,
                FileShare.ReadWrite,
                bufferSize: 4096,
                useAsync: true
            );

            await data.CopyToAsync(file);
        }

        public Task DeleteAsync(string path)
        {
            var fullPath = GetFullPath(path);

            if (File.Exists(fullPath))
                File.Delete(fullPath);
            else if (Directory.Exists(fullPath))
                Directory.Delete(fullPath, true);

            return Task.CompletedTask;
        }

        public async Task<FileMetadata> GetMetadataAsync(string path)
        {
            var fullPath = GetFullPath(path);
            var info = new FileInfo(fullPath);

            // ACLs aus DB laden, wenn ServiceProvider verfügbar
            IReadOnlyList<AccessEntry> acl = Array.Empty<AccessEntry>();

            if (_serviceProvider != null)
            {
                try
                {
                    using var scope = _serviceProvider.CreateScope();
                    var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

                    var dbMeta = await db.FileMetadata
                        .Include(m => m.Acl)
                        .FirstOrDefaultAsync(m => m.Path == path);

                    if (dbMeta?.Acl != null)
                        acl = dbMeta.Acl;
                }
                catch (Exception ex)
                {
                    // ACL-Laden fehlgeschlagen — wir loggen, aber brechen nicht ab.
                    // Ohne ACLs greift der Fallback in AclService (empty ACL = allow).
                    Console.WriteLine($"[FileSystemStorage] ACL load failed for '{path}': {ex.Message}");
                }
            }

            return new FileMetadata
            {
                Path = path,
                Name = info.Name,
                Size = info.Exists ? info.Length : 0,
                IsDirectory = Directory.Exists(fullPath),
                CreatedAt = info.Exists ? info.CreationTimeUtc : DateTime.UtcNow,
                ModifiedAt = info.Exists ? info.LastWriteTimeUtc : DateTime.UtcNow,
                Acl = acl
            };
        }
    }
}
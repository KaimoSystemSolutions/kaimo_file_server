using Kaimo_File_Server_Core.Core.Domain;
using Kaimo_File_Server_Core.Core.Storage;
using SMBLibrary;
using System;
using System.Collections.Generic;
using System.Text;
using static Kaimo_File_Server_Core.Core.Security.FilePermissions;

namespace Kaimo_File_Server_Core.Storage
{
    internal class FileSystemStorage : IStorageEngine
    {
        private readonly string _rootPath;

        /// <summary>
        /// Creates initially the new file system storage
        /// </summary>
        /// <param name="rootPath"></param>
        public FileSystemStorage(string rootPath)
        {
            _rootPath = rootPath;
        }

        private string GetFullPath(string path)
        {
            var root = Path.GetFullPath(_rootPath)
                .TrimEnd(Path.DirectorySeparatorChar)
                + Path.DirectorySeparatorChar;

            var full = Path.GetFullPath(Path.Combine(root, path));

            if (!full.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                throw new UnauthorizedAccessException();

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

        public Task<FileMetadata> GetMetadataAsync(string path)
        {
            var fullPath = GetFullPath(path);

            var info = new FileInfo(fullPath);

            return Task.FromResult(new FileMetadata
            {
                Path = path,
                Name = info.Name,
                Size = info.Exists ? info.Length : 0,
                IsDirectory = Directory.Exists(fullPath),
                CreatedAt = info.Exists ? info.CreationTimeUtc : DateTime.UtcNow,
                ModifiedAt = info.Exists ? info.LastWriteTimeUtc : DateTime.UtcNow
            });
        }
    }
}

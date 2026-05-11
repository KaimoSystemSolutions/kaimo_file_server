using Kaimo_File_Server.Core.Domain;
using Kaimo_File_Server.Core.Repositories;
using System.IO.Compression;
using System.Security.Cryptography;

namespace Kaimo_File_Server.Core.Services
{
    /// <summary>
    /// Core implementation of file versioning logic.
    /// 
    /// Storage layout for version blobs (content-addressable, compressed):
    ///   {versionStorageRoot}/AB/CD/ABCDEF1234567890...sha256.bin.gz
    /// 
    /// The first 2 bytes of the hash form subdirectories to avoid
    /// having millions of files in one folder.
    /// 
    /// Deduplication strategy:
    ///   - SHA-256 hash of the UNCOMPRESSED content
    ///   - If the latest version of a file has the same hash → skip (no new version)
    ///   - If a blob with that hash already exists on disk → reuse (no new write)
    ///   - Blobs are stored gzip-compressed to save disk space
    /// </summary>
    public class FileVersionService : IFileVersionService
    {
        private readonly IFileVersionRepository _versionRepo;
        private readonly string _versionStorageRoot;

        private readonly int _defaultMaxVersions;
        private readonly TimeSpan? _defaultMaxAge;

        public FileVersionService(
            IFileVersionRepository versionRepo,
            string versionStorageRoot,
            int defaultMaxVersions = 64,
            TimeSpan? defaultMaxAge = null)
        {
            _versionRepo = versionRepo;
            _versionStorageRoot = versionStorageRoot;
            _defaultMaxVersions = defaultMaxVersions;
            _defaultMaxAge = defaultMaxAge;

            Directory.CreateDirectory(_versionStorageRoot);
        }

        public async Task<FileVersion?> CreateVersionAsync(
    string filePath, Stream content, string? userId = null)
        {
            // ── 1. Hash the content ──
            content.Position = 0;
            var hash = await ComputeHashAsync(content);
            content.Position = 0;

            // ── 2. Skip if content unchanged since last version ──
            if (await _versionRepo.ExistsWithHashAsync(filePath, hash))
                return null;

            // ── 3. Store blob compressed (content-addressable) ──
            var blobRelativePath = HashToPath(hash);
            var blobFullPath = Path.Combine(_versionStorageRoot, blobRelativePath);

            long compressedSize;

            if (!File.Exists(blobFullPath))
            {
                Directory.CreateDirectory(Path.GetDirectoryName(blobFullPath)!);

                await using var fs = new FileStream(blobFullPath, FileMode.CreateNew,
                    FileAccess.Write, FileShare.None, 4096, true);
                await using var gzip = new GZipStream(fs, CompressionLevel.Optimal, leaveOpen: true);

                await content.CopyToAsync(gzip);
                await gzip.FlushAsync();

                compressedSize = fs.Length;
            }
            else
            {
                compressedSize = new FileInfo(blobFullPath).Length;
            }

            content.Position = 0;

            // ── 4. Create version record ──
            var versionNumber = await _versionRepo.GetMaxVersionNumberAsync(filePath) + 1;
            var now = DateTime.UtcNow;

            // Truncate to seconds — @GMT- format has second precision
            now = new DateTime(now.Year, now.Month, now.Day,
                now.Hour, now.Minute, now.Second, DateTimeKind.Utc);

            // If a version at this exact second already exists, bump to next free second
            var existingAtTimestamp = await _versionRepo.GetVersionAsync(filePath, now);
            while (existingAtTimestamp != null)
            {
                now = now.AddSeconds(1);
                existingAtTimestamp = await _versionRepo.GetVersionAsync(filePath, now);
            }

            var version = new FileVersion(
                filePath: filePath,
                snapshotTimestampUtc: now,
                storagePath: blobRelativePath,
                contentHash: hash,
                size: content.Length,
                createdBy: userId,
                versionNumber: versionNumber);

            await _versionRepo.CreateAsync(version);

            Console.WriteLine(
                $"[Versioning] v{versionNumber} for '{filePath}' " +
                $"({content.Length} → {compressedSize} bytes, " +
                $"{(double)compressedSize / Math.Max(content.Length, 1):P0} ratio)");

            // ── 5. Enforce retention ──
            await ApplyRetentionAsync(filePath, _defaultMaxVersions, _defaultMaxAge);

            return version;
        }

        public async Task<Stream> ReadVersionAsync(
            string filePath, DateTime snapshotTimestampUtc)
        {
            var version = await _versionRepo.GetVersionAsync(filePath, snapshotTimestampUtc);
            if (version == null)
                throw new FileNotFoundException(
                    $"No version found for '{filePath}' at {snapshotTimestampUtc:O}");

            var blobFullPath = Path.Combine(_versionStorageRoot, version.StoragePath);
            if (!File.Exists(blobFullPath))
                throw new FileNotFoundException(
                    $"Version blob missing: {version.StoragePath}");

            // Decompress into MemoryStream so the caller gets a seekable stream.
            // For very large files (>100MB) a temp file would be better,
            // but for typical office documents this is fine.
            var ms = new MemoryStream();
            await using (var fs = new FileStream(blobFullPath, FileMode.Open,
                FileAccess.Read, FileShare.Read, 4096, true))
            await using (var gzip = new GZipStream(fs, CompressionMode.Decompress))
            {
                await gzip.CopyToAsync(ms);
            }
            ms.Position = 0;
            return ms;
        }

        public async Task<List<FileVersion>> GetVersionsAsync(string filePath)
        {
            return await _versionRepo.GetVersionsAsync(filePath);
        }

        public async Task<List<DateTime>> GetSnapshotTimestampsAsync(string pathPrefix = "")
        {
            return await _versionRepo.GetAllSnapshotTimestampsAsync(pathPrefix);
        }

        public async Task<FileVersion?> GetVersionAtAsync(
            string filePath, DateTime snapshotTimestampUtc)
        {
            return await _versionRepo.GetVersionAsync(filePath, snapshotTimestampUtc);
        }

        public async Task<int> ApplyRetentionAsync(
            string filePath, int? maxVersions = null, TimeSpan? maxAge = null)
        {
            int deleted = 0;

            var effectiveMax = maxVersions ?? _defaultMaxVersions;
            var effectiveAge = maxAge ?? _defaultMaxAge;

            if (effectiveMax > 0)
                deleted += await _versionRepo.TrimToMaxVersionsAsync(filePath, effectiveMax);

            if (effectiveAge.HasValue)
            {
                var cutoff = DateTime.UtcNow - effectiveAge.Value;
                deleted += await _versionRepo.DeleteOlderThanAsync(filePath, cutoff);
            }

            return deleted;
        }

        // ── Helpers ──

        private static async Task<string> ComputeHashAsync(Stream stream)
        {
            using var sha256 = SHA256.Create();
            var hashBytes = await sha256.ComputeHashAsync(stream);
            return Convert.ToHexString(hashBytes);
        }

        /// <summary>
        /// Converts a hex hash to a compressed blob path with 2-level fan-out.
        /// "ABCDEF1234..." → "AB/CD/ABCDEF1234....bin.gz"
        /// </summary>
        private static string HashToPath(string hash)
        {
            var dir1 = hash[..2];
            var dir2 = hash[2..4];
            return Path.Combine(dir1, dir2, hash + ".bin.gz");
        }
    }
}
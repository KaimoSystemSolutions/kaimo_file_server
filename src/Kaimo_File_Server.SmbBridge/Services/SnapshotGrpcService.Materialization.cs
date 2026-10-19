using System.Security.Cryptography;
using Kaimo_File_Server.Core.Domain;
using Kaimo_File_Server.Core.Helpers;

namespace Kaimo_File_Server.SmbBridge.Services;

public partial class SnapshotGrpcService
{
    /// <summary>
    /// Writes one version's decompressed content into the isolated snapshot cache
    /// (<c>&lt;cache-root&gt;/&lt;share-id&gt;/&lt;@GMT&gt;/&lt;user-id&gt;/&lt;filePath&gt;</c>) and stamps
    /// the historical modification time onto it. Idempotent: a version is
    /// content-addressed and immutable, so an existing cache file with the exact
    /// expected size and SHA-256 hash is reused; the mtime is (re)applied every
    /// call. New content is verified in a same-directory temporary file and
    /// atomically published, so readers never observe a partial final file.
    ///
    /// The historical mtime matters twice: Windows "Previous Versions" HIDES any
    /// snapshot whose file mtime equals the live file's, and distinct per-version
    /// mtimes let Explorer tell the versions apart. Returns a cache-root-relative
    /// path (forward slashes); the VFS validates it and joins its configured root.
    /// </summary>
    private async Task<string> MaterializeVersionAsync(
        Guid shareId, string gmtToken,
        string cacheScope, FileVersion v,
        CancellationToken cancellationToken)
    {
        if (!ShareRelativePath.TryNormalizeStrict(
                v.FilePath, out string normalizedPath,
                allowRoot: false, allowInternalNamespace: false))
            throw new InvalidDataException("Version contains an invalid file path.");
        string shareScope = SnapshotCache.RelativeShareRootFor(shareId);
        string rel = $"{shareScope}/{gmtToken}/{cacheScope}/{normalizedPath}";
        string scopeRoot = SnapshotCache.EnsureUserScope(
            _cacheRoot, shareId, gmtToken, cacheScope);
        string full = GetScopedCachePath(scopeRoot, normalizedPath);

        ValidateVersionContentMetadata(v);
        bool reusable = await HasExpectedContentAsync(
            full, v.Size, v.ContentHash, cancellationToken);
        if (!reusable)
        {
            SnapshotCache.EnsureDirectory(Path.GetDirectoryName(full)!);
            string temporary = full + ".kaimo-tmp-" + Guid.NewGuid().ToString("N");
            try
            {
                await using var content = await _versions.ReadVersionAsync(
                    shareId, v.FilePath, v.SnapshotTimestampUtc,
                    cancellationToken);
                await WriteVerifiedTemporaryFileAsync(
                    content, temporary, v.Size, v.ContentHash,
                    cancellationToken);

                SnapshotCache.SetReadOnlyProjectionMode(temporary);
                File.SetLastWriteTimeUtc(temporary, v.SnapshotTimestampUtc);

                // The temporary file lives beside the destination, so rename is
                // atomic and cannot cross filesystem boundaries. Concurrent
                // publishers may replace one another, but only after both have
                // independently verified the same immutable version content.
                File.Move(temporary, full, overwrite: true);
            }
            finally
            {
                try
                {
                    File.Delete(temporary);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex,
                        "Failed to remove abandoned snapshot temporary file {Path}",
                        temporary);
                }
            }
        }
        SnapshotCache.SetReadOnlyProjectionMode(full);

        // Best-effort: stamp the historical mtime (what Windows "Previous Versions"
        // uses to tell versions apart / hide the one identical to the live file).
        // Creation time (birthtime) is deliberately NOT set — some bind-mount
        // filesystems (drvfs/9p on Docker Desktop) cannot set it and throw, which
        // must never break materialization. mtime failures are likewise non-fatal.
        try
        {
            File.SetLastWriteTimeUtc(full, v.SnapshotTimestampUtc);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "SetLastWriteTimeUtc failed for {Path} (non-fatal)", full);
        }
        return rel;
    }

    private static void ValidateVersionContentMetadata(FileVersion version)
    {
        if (version.Size < 0)
            throw new InvalidDataException("Version contains a negative size.");
        if (string.IsNullOrEmpty(version.ContentHash) ||
            version.ContentHash.Length != 64 ||
            !version.ContentHash.All(Uri.IsHexDigit))
            throw new InvalidDataException(
                "Version contains an invalid SHA-256 content hash.");
    }

    private static async Task<bool> HasExpectedContentAsync(
        string path, long expectedLength, string expectedHash,
        CancellationToken cancellationToken)
    {
        var existing = new FileInfo(path);
        if (!existing.Exists || existing.Length != expectedLength)
            return false;

        await using var stream = new FileStream(
            path, FileMode.Open, FileAccess.Read, FileShare.Read,
            bufferSize: 81920, useAsync: true);
        string actualHash = Convert.ToHexString(
            await SHA256.HashDataAsync(stream, cancellationToken));
        return string.Equals(
            actualHash, expectedHash, StringComparison.OrdinalIgnoreCase);
    }

    private static async ValueTask<bool> HasExpectedProjectionAsync(
        string scopeRoot,
        string directory,
        IReadOnlyCollection<FileVersion> expectedVersions,
        CancellationToken cancellationToken)
    {
        if (!Directory.Exists(directory))
            return false;

        var expectedFiles = new Dictionary<string, FileVersion>(
            OperatingSystem.IsWindows()
                ? StringComparer.OrdinalIgnoreCase
                : StringComparer.Ordinal);
        foreach (FileVersion version in expectedVersions)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!ShareRelativePath.TryNormalizeStrict(
                    version.FilePath, out _,
                    allowRoot: false, allowInternalNamespace: false))
                return false;
            ValidateVersionContentMetadata(version);
            string fullPath = GetScopedCachePath(
                scopeRoot, version.FilePath);
            expectedFiles[fullPath] = version;
        }

        string[] actualFiles;
        try
        {
            actualFiles = Directory.GetFiles(
                directory, "*", SearchOption.AllDirectories);
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }

        if (actualFiles.Length != expectedFiles.Count)
            return false;

        foreach ((string path, FileVersion version) in expectedFiles)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!await HasExpectedContentAsync(
                    path, version.Size, version.ContentHash,
                    cancellationToken))
                return false;
        }
        return true;
    }

    private static async Task WriteVerifiedTemporaryFileAsync(
        Stream content,
        string temporaryPath,
        long expectedLength,
        string expectedHash,
        CancellationToken cancellationToken)
    {
        await using var output = new FileStream(
            temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None,
            bufferSize: 81920,
            options: FileOptions.Asynchronous | FileOptions.SequentialScan);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        byte[] buffer = new byte[81920];
        long written = 0;

        while (true)
        {
            int read = await content.ReadAsync(
                buffer.AsMemory(), cancellationToken);
            if (read == 0)
                break;
            if (written > expectedLength - read)
                throw new InvalidDataException(
                    "Version content exceeds its declared size.");

            await output.WriteAsync(
                buffer.AsMemory(0, read), cancellationToken);
            hash.AppendData(buffer, 0, read);
            written += read;
        }

        if (written != expectedLength)
            throw new InvalidDataException(
                $"Version content length {written} does not match declared size {expectedLength}.");

        string actualHash = Convert.ToHexString(hash.GetHashAndReset());
        if (!string.Equals(
                actualHash, expectedHash, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException(
                "Version content does not match its declared SHA-256 hash.");

        await output.FlushAsync(cancellationToken);
        output.Flush(flushToDisk: true);
    }
}

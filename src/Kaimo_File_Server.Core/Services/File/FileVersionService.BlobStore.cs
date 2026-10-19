using Kaimo_File_Server.Core.Domain;
using Microsoft.Extensions.Logging;
using System.IO.Compression;
using System.Security.Cryptography;

namespace Kaimo_File_Server.Core.Services.File;

public partial class FileVersionService
{
    // ------------------ Helpers ------------------

    private static async Task<string> ComputeHashAsync(Stream stream)
    {
        using var sha256 = SHA256.Create();
        var hashBytes = await sha256.ComputeHashAsync(stream);
        return Convert.ToHexString(hashBytes);
    }

    private static async Task<long> WriteValidatedBlobAsync(
        string blobFullPath, Stream content, string expectedHash, long expectedSize)
    {
        var directory = Path.GetDirectoryName(blobFullPath)!;
        Directory.CreateDirectory(directory);
        var tempPath = Path.Combine(
            directory,
            $".{Path.GetFileName(blobFullPath)}.{Guid.NewGuid():N}.tmp");

        try
        {
            await using (var fs = new FileStream(
                tempPath, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                81920, FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                await using (var gzip = new GZipStream(
                    fs, CompressionLevel.Optimal, leaveOpen: true))
                {
                    await content.CopyToAsync(gzip);
                }

                await fs.FlushAsync();
                fs.Flush(flushToDisk: true);
            }

            await ValidateBlobAsync(
                tempPath, expectedHash, expectedSize, Stream.Null,
                CancellationToken.None, throwOnInvalid: true);

            var compressedSize = new FileInfo(tempPath).Length;
            System.IO.File.Move(tempPath, blobFullPath, overwrite: true);
            return compressedSize;
        }
        finally
        {
            try { System.IO.File.Delete(tempPath); }
            catch { /* preserve the compression/validation exception */ }
        }
    }

    private static async Task<bool> ValidateBlobAsync(
        string blobFullPath, string expectedHash, long expectedSize, Stream output,
        CancellationToken cancellationToken = default, bool throwOnInvalid = false)
    {
        try
        {
            await using var fs = new FileStream(
                blobFullPath, FileMode.Open, FileAccess.Read,
                FileShare.Read | FileShare.Delete, 81920,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            await using var gzip = new GZipStream(fs, CompressionMode.Decompress);
            using var hasher = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);

            var buffer = new byte[81920];
            long actualSize = 0;
            while (true)
            {
                var read = await gzip.ReadAsync(buffer, cancellationToken);
                if (read == 0) break;

                actualSize = checked(actualSize + read);
                hasher.AppendData(buffer, 0, read);
                await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
            }

            var actualHash = Convert.ToHexString(hasher.GetHashAndReset());
            if (actualSize == expectedSize
                && string.Equals(actualHash, expectedHash, StringComparison.OrdinalIgnoreCase))
                return true;

            if (!throwOnInvalid)
                return false;

            throw new InvalidDataException(
                $"Version blob integrity check failed. Expected {expectedSize} bytes/{expectedHash}, " +
                $"got {actualSize} bytes/{actualHash}.");
        }
        catch (InvalidDataException) when (!throwOnInvalid)
        {
            return false;
        }
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

    private async Task DeleteUnreferencedBlobsAsync(IEnumerable<FileVersion> removed)
    {
        // The removed rows' share may already be gone, so every known root is checked.
        foreach (var storagePath in removed.Select(v => v.StoragePath).Distinct(StringComparer.Ordinal))
            foreach (var root in KnownRoots)
                await DeleteBlobIfUnneededAsync(root, storagePath);
    }

    /// <summary>Deletes one blob copy when no version still needs it in that root.</summary>
    private async Task<bool> DeleteBlobIfUnneededAsync(string storageRoot, string storagePath)
    {
        var fullPath = Path.GetFullPath(Path.Combine(storageRoot, storagePath));
        var root = Path.GetFullPath(storageRoot)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            + Path.DirectorySeparatorChar;
        if (!fullPath.StartsWith(root, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Version blob path escaped the storage root.");
        if (!System.IO.File.Exists(fullPath))
            return false;

        var blobLock = GetBlobLock(fullPath);
        await blobLock.WaitAsync();
        try
        {
            if (await IsBlobNeededInRootAsync(storageRoot, storagePath)) return false;
            System.IO.File.Delete(fullPath);
            RemoveEmptyParentDirectories(Path.GetDirectoryName(fullPath), root);
            return true;
        }
        catch (Exception ex)
        {
            // The DB is authoritative. Do not turn an already completed user-file
            // operation into a failure when an external process temporarily locks a blob.
            _logger.LogWarning(ex, "Failed to delete unreferenced version blob {StoragePath}", storagePath);
            return false;
        }
        finally
        {
            blobLock.Release();
        }
    }

    /// <summary>
    /// Whether the copy of a blob in <paramref name="storageRoot"/> is still needed: a
    /// share of that pool references it, a referencing share cannot be resolved (kept
    /// conservatively), or — for the legacy store — a referencing share's pool does not
    /// have its own copy yet.
    /// </summary>
    private async Task<bool> IsBlobNeededInRootAsync(string storageRoot, string storagePath)
    {
        if (_locator is null)
            return await _versionRepo.IsStoragePathReferencedAsync(storagePath);

        bool isLegacy = SamePath(storageRoot, _versionStorageRoot);
        foreach (var shareId in await _versionRepo.GetShareIdsReferencingStoragePathAsync(storagePath))
        {
            var shareRoot = await _locator.GetRootAsync(shareId);
            if (shareRoot is null || SamePath(shareRoot, storageRoot))
                return true;
            if (isLegacy && !System.IO.File.Exists(Path.Combine(shareRoot, storagePath)))
                return true;
        }
        return false;
    }

    // ------------------ Storage location ------------------

    private async Task<string?> ResolveRootAsync(Guid shareId)
        => _locator is null ? _versionStorageRoot : await _locator.GetRootAsync(shareId);

    /// <summary>Every root that may hold blobs: the pools first, then the legacy store.</summary>
    private IEnumerable<string> KnownRoots => _locator is null
        ? [_versionStorageRoot]
        : _locator.AllRoots.Append(_versionStorageRoot);

    private (string Root, string FullPath)? FindBlob(string? preferredRoot, string storagePath)
    {
        var roots = preferredRoot is null ? KnownRoots : KnownRoots.Prepend(preferredRoot);
        foreach (var root in roots)
        {
            var fullPath = Path.Combine(root, storagePath);
            if (System.IO.File.Exists(fullPath))
                return (root, fullPath);
        }
        return null;
    }

    private bool HasRoomFor(string root, long bytes)
    {
        try
        {
            var (free, total) = _diskSpace(root);
            var reserve = Math.Max(MinFreeBytes, (long)(total * MinFreeRatio));
            return free - bytes >= reserve;
        }
        catch (Exception ex)
        {
            // Fail closed: an unknown fill level must not risk filling the volume.
            _logger.LogWarning(ex, "Could not determine the free space of version storage {Root}", root);
            return false;
        }
    }

    private static (long Free, long Total) ProbeDiskSpace(string path)
    {
        var drive = new DriveInfo(path);
        return (drive.AvailableFreeSpace, drive.TotalSize);
    }

    /// <summary>Rate limit for warnings that would otherwise fire once per written file.</summary>
    private static bool ShouldWarn(ref long lastWarningTicks)
    {
        var now = Environment.TickCount64;
        var last = Interlocked.Read(ref lastWarningTicks);
        return now - last >= WarningIntervalMs
               && Interlocked.CompareExchange(ref lastWarningTicks, now, last) == last;
    }

    private static bool SamePath(string a, string b)
        => string.Equals(
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(a)),
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(b)),
            PathComparison);
}

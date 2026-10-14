using Kaimo_File_Server.Core.Domain;
using Kaimo_File_Server.Core.Helpers;
using Kaimo_File_Server.Core.Repositories;
using Kaimo_File_Server.Core.Services;
using Kaimo_File_Server.Search;
using Microsoft.Extensions.Logging;

namespace Kaimo_File_Server.Infrastructure.Services;

public enum SharePoolMoveResult
{
    Moved,
    /// <summary>The share already lives in the target pool.</summary>
    Unchanged,
    SourceMissing,
    DestinationExists,
    /// <summary>Another management operation or a cloud sync holds the share.</summary>
    InUse,
    InsufficientSpace,
    Failed,
}

/// <param name="Required">Bytes the copy needs (with headroom); only for <see cref="SharePoolMoveResult.InsufficientSpace"/>.</param>
/// <param name="Available">Free bytes in the target pool; only for <see cref="SharePoolMoveResult.InsufficientSpace"/>.</param>
public sealed record SharePoolMoveOutcome(SharePoolMoveResult Result, long Required = 0, long Available = 0);

/// <summary>
/// Moves a share folder into another storage pool and repoints the share: an atomic rename
/// when both pools share a file system, otherwise a space-reserved copy into a staging folder
/// that is published atomically, after which the source is removed. Used by share management
/// and by the home-folder pool move.
///
/// The share is disabled for the duration of the move (and its previous state restored), so
/// no transport writes into a source that is about to disappear: SMB drops a disabled share
/// within one share sync and disconnects its sessions, Web/REST/WebDAV refuse it.
/// ACLs, metadata, versions and links are share-relative and need no change; version blobs
/// follow via <c>VersionStorageReconcilerService</c>, Samba and restic read the new path.
/// Callers authorize and validate that <c>targetPoolPath</c> is a configured pool.
/// </summary>
public sealed class SharePoolMoveService(
    IShareRepository shares,
    ShareLockManager lockManager,
    ILogger<SharePoolMoveService> logger,
    ISearchService? searchService = null,
    ICloudSyncOperationCoordinator? cloudSyncOperations = null)
{
    private static readonly StringComparer PathComparer =
        OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    private const string ReservationFilePrefix = ShareEntryPolicy.InternalNamespacePrefix + "reserve-";

    /// <summary>
    /// Wait after disabling the share before touching its folder: covers the SMB share sync
    /// interval (2 s by default) plus its session disconnect.
    /// </summary>
    internal TimeSpan DisableGracePeriod { get; init; } = TimeSpan.FromSeconds(5);

    internal Action<string, string> RenameDirectory { get; init; } = Directory.Move;

    internal Func<string, long> AvailableFreeSpace { get; init; } = path => new DriveInfo(path).AvailableFreeSpace;

    public async Task<SharePoolMoveOutcome> MoveAsync(ShareDefinition share, string targetPoolPath)
    {
        var sourcePath = Path.GetFullPath(share.Path);
        var destinationPath = Path.Combine(targetPoolPath, share.Name);
        var sourceParent = Path.GetDirectoryName(sourcePath);

        if (sourceParent is not null
            && PathComparer.Equals(Path.GetFullPath(sourceParent), Path.GetFullPath(targetPoolPath)))
            return new(SharePoolMoveResult.Unchanged);
        if (!Directory.Exists(sourcePath))
            return new(SharePoolMoveResult.SourceMissing);
        if (Directory.Exists(destinationPath) || File.Exists(destinationPath))
            return new(SharePoolMoveResult.DestinationExists);

        var shareLock = lockManager.GetLock(share.Name);
        if (!await shareLock.WaitAsync(TimeSpan.FromSeconds(30)))
            return new(SharePoolMoveResult.InUse);

        try
        {
            ICloudSyncOperationLease? lease = null;
            if (cloudSyncOperations is not null
                && (lease = await cloudSyncOperations.TryBeginShareMutationAsync(share.Id)) is null)
                return new(SharePoolMoveResult.InUse);
            await using var cloudOperationLease = lease;

            var wasEnabled = share.IsEnabled;
            try
            {
                if (wasEnabled)
                {
                    share.IsEnabled = false;
                    await shares.UpdateAsync(share);
                    await Task.Delay(DisableGracePeriod);
                }

                await MoveCoreAsync(share, sourcePath, destinationPath);
            }
            finally
            {
                if (wasEnabled)
                    await RestoreEnabledAsync(share);
            }

            logger.LogInformation(
                "Share '{ShareName}' moved from '{SourcePath}' to storage pool '{PoolPath}'",
                share.Name, sourcePath, targetPoolPath);
            return new(SharePoolMoveResult.Moved);
        }
        catch (InsufficientPoolSpaceException ex)
        {
            logger.LogWarning(
                "Share '{ShareName}' not moved: target pool '{PoolPath}' has {Available} bytes free, {Required} bytes required",
                share.Name, targetPoolPath, ex.Available, ex.Required);
            return new(SharePoolMoveResult.InsufficientSpace, ex.Required, ex.Available);
        }
        catch (Exception ex)
        {
            logger.LogError(ex,
                "Error moving share '{ShareName}' from '{SourcePath}' to '{DestinationPath}'",
                share.Name, sourcePath, destinationPath);
            return new(SharePoolMoveResult.Failed);
        }
        finally
        {
            shareLock.Release();
        }
    }

    private async Task MoveCoreAsync(ShareDefinition share, string sourcePath, string destinationPath)
    {
        var sourceWasCopied = await MoveDirectoryAcrossPoolsAsync(sourcePath, destinationPath);

        try
        {
            await shares.UpdateLocationAsync(share.Id, share.Name, destinationPath);
            share.Path = destinationPath;
        }
        catch
        {
            // Keep the persisted path usable when the DB update fails.
            share.Path = sourcePath;
            if (sourceWasCopied)
                Directory.Delete(destinationPath, recursive: true);
            else
                RenameDirectory(destinationPath, sourcePath);
            throw;
        }

        if (sourceWasCopied)
        {
            try
            {
                Directory.Delete(sourcePath, recursive: true);
            }
            catch (Exception cleanupEx)
            {
                // The DB already points at the complete target copy. A
                // stale source copy is safe and can be removed manually.
                logger.LogWarning(cleanupEx,
                    "Share '{ShareName}' moved successfully, but old path '{SourcePath}' could not be removed",
                    share.Name, sourcePath);
            }
        }

        if (searchService is not null)
        {
            try
            {
                await searchService.onDirectoryRenamed(sourcePath, destinationPath);
            }
            catch (Exception searchEx)
            {
                logger.LogWarning(searchEx,
                    "Share '{ShareName}' moved, but its search index could not be updated",
                    share.Name);
            }
        }
    }

    private async Task RestoreEnabledAsync(ShareDefinition share)
    {
        share.IsEnabled = true;
        try
        {
            await shares.UpdateAsync(share);
        }
        catch (Exception ex)
        {
            logger.LogError(ex,
                "Share '{ShareName}' could not be re-enabled after its pool move; enable it manually",
                share.Name);
        }
    }

    /// <returns>
    /// <c>true</c> when a cross-filesystem copy was required and the source still
    /// exists; <c>false</c> when an atomic directory rename moved the source.
    /// </returns>
    private async Task<bool> MoveDirectoryAcrossPoolsAsync(string sourcePath, string destinationPath)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)
            ?? throw new InvalidOperationException("Destination has no parent directory."));

        try
        {
            RenameDirectory(sourcePath, destinationPath);
            return false;
        }
        catch (IOException)
        {
            // A rename cannot cross filesystem/mount boundaries. Copy into a
            // private staging directory first, publish it atomically in the
            // target pool, then remove the old share only after the copy exists.
        }

        var targetPool = Path.GetDirectoryName(destinationPath)!;
        var required = GetDirectorySize(sourcePath);
        // ponytail: fixed headroom (block overhead + never fill a pool to 0 bytes);
        // make it configurable if pools differ a lot in size.
        var headroom = Math.Max(1L << 30, required / 50);
        var available = AvailableFreeSpace(targetPool);
        if (required + headroom > available)
            throw new InsufficientPoolSpaceException(required + headroom, available);

        var reserved = required + headroom;
        var reservation = ReservePoolSpace(targetPool, reserved, available);
        // Bytes already released from the reservation but not yet copied.
        var credit = 0L;
        var chunk = Math.Max(256L << 20, required / 20);

        var stagingPath = destinationPath + ".kaimo-moving-" + Guid.NewGuid().ToString("N");
        try
        {
            // Hand reserved space over to the copy BEFORE it is written, so the copy
            // never needs twice the space and nobody else can fill the pool meanwhile.
            // Released in chunks by closing and re-reserving smaller: shrinking an open
            // file frees nothing on 9p/drvfs (Docker Desktop) until it is closed.
            await CopyDirectoryAsync(sourcePath, stagingPath, length =>
            {
                if (credit < length)
                {
                    var release = Math.Min(Math.Max(chunk, length - credit), reserved - headroom);
                    if (release > 0)
                    {
                        reserved -= release;
                        credit += release;
                        reservation.Dispose();
                        reservation = ReservePoolSpace(targetPool, reserved, AvailableFreeSpace(targetPool));
                    }
                }
                // Clamped: files that grew since sizing are covered by the headroom.
                credit = Math.Max(0, credit - length);
            });
            Directory.Move(stagingPath, destinationPath);
            return true;
        }
        catch
        {
            if (Directory.Exists(stagingPath))
                Directory.Delete(stagingPath, recursive: true);
            throw;
        }
        finally
        {
            reservation.Dispose();
        }
    }

    /// <summary>
    /// Allocates <paramref name="bytes"/> in the target pool through a placeholder
    /// file in the pool root. On Linux the preallocation is a real <c>fallocate</c>, so
    /// the space is taken from every other writer (other moves, uploads, SMB) at once
    /// and an overbooked pool fails here atomically. The file is deleted on close.
    /// It deliberately stays visible while open: unlinking it early breaks on 9p/drvfs
    /// bind mounts (Docker Desktop), where an unlinked file's handle returns ENOENT.
    /// Leftovers from a crash are removed by <see cref="DeleteStaleReservations"/>.
    /// ponytail: on filesystems without fallocate the file stays sparse and only the
    /// free-space check protects the pool.
    /// </summary>
    private static FileStream ReservePoolSpace(string poolPath, long bytes, long available)
    {
        var path = Path.Combine(poolPath, ReservationFilePrefix + Guid.NewGuid().ToString("N"));
        FileStream? stream = null;
        try
        {
            stream = new FileStream(path, new FileStreamOptions
            {
                Mode = FileMode.CreateNew,
                Access = FileAccess.Write,
                Share = FileShare.None,
                Options = FileOptions.DeleteOnClose,
                PreallocationSize = bytes
            });
            stream.SetLength(bytes);
            return stream;
        }
        catch (IOException ex)
        {
            stream?.Dispose();
            File.Delete(path);
            // Someone else took the space between the check and the allocation.
            throw new InsufficientPoolSpaceException(bytes, available, ex);
        }
    }

    /// <summary>
    /// Removes reservation files a crashed move left in the pool roots. Call once at
    /// startup, before any move can run: moves only happen inside this process.
    /// </summary>
    public static void DeleteStaleReservations(IEnumerable<string> poolPaths)
    {
        foreach (var pool in poolPaths)
        {
            try
            {
                foreach (var file in Directory.EnumerateFiles(pool, ReservationFilePrefix + "*"))
                    File.Delete(file);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Best effort: a leftover only costs space until the next start.
            }
        }
    }

    /// <summary>Total size of the files a move copies: links are recreated, not followed.</summary>
    public static long GetDirectorySize(string path)
        => new DirectoryInfo(path)
            .EnumerateFiles("*", new EnumerationOptions
            {
                RecurseSubdirectories = true,
                AttributesToSkip = FileAttributes.ReparsePoint,
                IgnoreInaccessible = false
            })
            .Sum(file => file.Length);

    private sealed class InsufficientPoolSpaceException(
        long required, long available, Exception? inner = null)
        : IOException("Not enough free space in the target storage pool.", inner)
    {
        public long Required { get; } = required;
        public long Available { get; } = available;
    }

    private static async Task CopyDirectoryAsync(
        string sourcePath, string destinationPath, Action<long> beforeFileCopy)
    {
        Directory.CreateDirectory(destinationPath);

        foreach (var entry in new DirectoryInfo(sourcePath).EnumerateFileSystemInfos())
        {
            var destinationEntry = Path.Combine(destinationPath, entry.Name);

            // Preserve links as links. Following a link could copy data outside
            // the share or recurse forever through a directory cycle.
            if (entry.LinkTarget is not null)
            {
                if (entry is DirectoryInfo)
                    Directory.CreateSymbolicLink(destinationEntry, entry.LinkTarget);
                else
                    File.CreateSymbolicLink(destinationEntry, entry.LinkTarget);
                continue;
            }

            if (entry is DirectoryInfo directory)
            {
                await CopyDirectoryAsync(directory.FullName, destinationEntry, beforeFileCopy);
                continue;
            }

            beforeFileCopy(((FileInfo)entry).Length);
            await using var source = new FileStream(
                entry.FullName, FileMode.Open, FileAccess.Read, FileShare.Read,
                bufferSize: 1024 * 1024, useAsync: true);
            await using var destination = new FileStream(
                destinationEntry, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                bufferSize: 1024 * 1024, useAsync: true);
            await source.CopyToAsync(destination);
            File.SetLastWriteTimeUtc(
                destinationEntry, File.GetLastWriteTimeUtc(entry.FullName));
        }

        Directory.SetLastWriteTimeUtc(
            destinationPath, Directory.GetLastWriteTimeUtc(sourcePath));
    }
}

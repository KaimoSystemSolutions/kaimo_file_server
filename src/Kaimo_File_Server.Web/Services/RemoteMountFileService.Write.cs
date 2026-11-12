using Kaimo_File_Server.Core.Domain;
using Kaimo_File_Server.Core.Domain.Identity;
using Kaimo_File_Server.Core.Helpers;
using Kaimo_File_Server.Core.Services.ExternalStorage;
using Kaimo_File_Server.Core.Services.File;
using Kaimo_File_Server.Infrastructure.ExternalStorage.Protocols;

namespace Kaimo_File_Server.Web.Services;

public sealed partial class RemoteMountFileService
{
    public Task WriteFileAsync(string path, Stream data, UserContext user, CancellationToken cancellationToken = default)
        => WriteFileAsync(path, data, user, null, null, cancellationToken);

    public Task WriteFileAsync(
        string path, Stream data, UserContext user, DateTime? modifiedAtUtc, CancellationToken cancellationToken = default)
        => WriteFileAsync(path, data, user, modifiedAtUtc, null, cancellationToken);

    public async Task WriteFileAsync(
        string path, Stream data, UserContext user, DateTime? modifiedAtUtc, DateTime? createdAtUtc,
        CancellationToken cancellationToken = default)
    {
        var remote = ToRemoteItem(path);
        var (share, files) = await OpenForWriteAsync(StorageProviderCapabilities.Write);
        var target = CloudAccessRemoteSessionOpener.RemotePath(share, remote);
        var times = modifiedAtUtc is { } modified
            ? TimesToStamp(await FindRemoteAsync(remote), modified, createdAtUtc)
            : null;
        await using var spool = await SpoolAsync(data, cancellationToken);
        await MutateAsync(async () =>
        {
            await files.WriteAsync(target, spool ?? data, overwrite: true, cancellationToken);
            if (times is { } stamp)
                await TryStampAsync(() => files.TrySetTimesAsync(target, stamp.Modified, stamp.Created, cancellationToken));
        });
    }

    /// <summary>
    /// A body still arriving from a client goes to a temp file first: a provider may truncate the
    /// target on open (SFTP), and remote storage keeps no version to fall back on.
    /// </summary>
    // ponytail: covers a failing client only; losing the provider connection mid-write can still truncate on SFTP. Write to a temp name and rename in the store if that shows up.
    private static async Task<FileStream?> SpoolAsync(Stream data, CancellationToken cancellationToken)
    {
        if (data.CanSeek) return null;
        var spool = ProtocolTemporaryFile.CreateSpool();
        try
        {
            await data.CopyToAsync(spool, cancellationToken);
            spool.Position = 0;
            return spool;
        }
        catch
        {
            await spool.DisposeAsync();
            throw;
        }
    }

    /// <summary>
    /// The contract of <see cref="IFileService"/>: a new file takes the source times; an overwrite
    /// moves Modified only forward (otherwise the file keeps its write time) and Created only back.
    /// </summary>
    private static (DateTime Modified, DateTime? Created)? TimesToStamp(FileMetadata? existing, DateTime modified, DateTime? created)
    {
        if (existing is null) return (modified, created);
        if (modified <= existing.ModifiedAt.ToUniversalTime()) return null;
        return (modified, created < existing.CreatedAt.ToUniversalTime() ? created : null);
    }

    public Task CreateFileAsync(string path, UserContext user)
        => WriteFileAsync(path, Stream.Null, user);

    public async Task CreateDirectoryAsync(string path, UserContext user)
    {
        var remote = ToRemoteItem(path);
        var (share, files) = await OpenForWriteAsync(StorageProviderCapabilities.CreateDirectory);
        await MutateAsync(() => files.CreateDirectoryAsync(CloudAccessRemoteSessionOpener.RemotePath(share, remote)));
    }

    /// <summary>Always permanent: remote storage has no recycle bin, whatever the host share enables.</summary>
    public async Task DeleteFileAsync(string path, UserContext user, bool isRecycleEnabled)
    {
        var remote = ToRemoteItem(path);
        var (share, files) = await OpenForWriteAsync(StorageProviderCapabilities.Delete);
        await MutateAsync(() => files.DeleteAsync(CloudAccessRemoteSessionOpener.RemotePath(share, remote), recursive: true));
    }

    public async Task RenameAsync(string oldPath, string newPath, UserContext user)
    {
        var from = ToRemoteItem(oldPath);
        var to = ToRemoteItem(newPath);
        var sameFolder = string.Equals(
            ShareRelativePath.GetParent(from), ShareRelativePath.GetParent(to), StringComparison.Ordinal);
        var (share, files) = await OpenForWriteAsync(
            sameFolder ? StorageProviderCapabilities.Rename : StorageProviderCapabilities.Move);
        await MutateAsync(() => files.MoveAsync(
            CloudAccessRemoteSessionOpener.RemotePath(share, from),
            CloudAccessRemoteSessionOpener.RemotePath(share, to)));
    }

    public Task SetModifiedAtAsync(string path, UserContext user, DateTime time)
        => SetModifiedAtAsync(path, user, time, null);

    /// <summary>Best effort: a provider that cannot set times, or a call without a modification time, is a no-op.</summary>
    public async Task SetModifiedAtAsync(string path, UserContext user, DateTime? modifiedAtUtc, DateTime? createdAtUtc)
    {
        var remote = ToRemote(path);
        var (share, files) = await OpenForWriteAsync(StorageProviderCapabilities.Write);
        if (remote.Length == 0 || modifiedAtUtc is not { } modified) return;

        var item = await FindRemoteAsync(remote) ?? throw new FileNotFoundException("The item does not exist.");
        var target = CloudAccessRemoteSessionOpener.RemotePath(share, remote);
        await MutateAsync(() => TryStampAsync(() => item.IsDirectory
            ? files.TrySetDirectoryTimesAsync(target, modified, createdAtUtc)
            : files.TrySetTimesAsync(target, modified, createdAtUtc)));
    }

    /// <summary>Runs a change; the cached listings are dropped even when it fails halfway.</summary>
    private async Task MutateAsync(Func<Task> change)
    {
        try
        {
            await GuardAsync(change);
        }
        finally
        {
            cache.InvalidateShare(mount.ShareId);
        }
    }

    private static async Task TryStampAsync(Func<Task> stamp)
    {
        try
        {
            await stamp();
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // Timestamps are best effort: the content is written, the provider keeps its own time.
        }
    }
}

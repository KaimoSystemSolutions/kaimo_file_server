using Kaimo_File_Server.Core.Domain;
using Kaimo_File_Server.Core.Domain.Identity;
using Kaimo_File_Server.Core.Helpers;
using Kaimo_File_Server.Core.Services.ExternalStorage;
using Kaimo_File_Server.Core.Services.File;

namespace Kaimo_File_Server.Web.Services;

/// <summary>
/// Presents a virtual share mounted as a folder as an <see cref="IFileService"/>, so REST and
/// WebDAV reach the provider through their existing code. Paths stay host-share-relative
/// (<c>Team/Cloud/a.txt</c>): the mount path is stripped on the way in and put back on the way out.
/// Lives for one request; <see cref="MountAwareFileServiceRouter"/> creates and disposes it.
/// </summary>
/// <remarks>Rights are fixed by <see cref="MountedFolder.Permission"/>, the user arguments are unused.</remarks>
public sealed partial class RemoteMountFileService(
    MountedFolder mount,
    CloudAccessRemoteSessionOpener opener,
    CloudAccessDirectoryCache cache,
    bool freshListings = false) : IFileService, IAsyncDisposable
{
    // ponytail: one provider session per request; pool per connection if SFTP handshakes measurably slow WebDAV.
    private Task<(CloudAccessShare Share, IStorageSession Session)>? _opening;

    /// <summary>The mount point as an entry of its parent listing, or as its own metadata.</summary>
    public static FileMetadata MountEntry(MountedFolder mount) => new()
    {
        ShareId = mount.ShareId,
        Path = mount.MountPath,
        Name = mount.Name,
        IsDirectory = true,
        CreatedAt = DateTime.UnixEpoch,
        ModifiedAt = DateTime.UnixEpoch
    };

    public Task<bool> CanReadAsync(string path, UserContext user) => HasCapabilityAsync(path, StorageProviderCapabilities.Read);

    public Task<bool> CanListAsync(string path, UserContext user) => HasCapabilityAsync(path, StorageProviderCapabilities.Browse);

    public Task<bool> CanWriteAsync(string path, UserContext user) => MayWriteAsync(path, StorageProviderCapabilities.Write);

    public Task<bool> CanCreateAsync(string path, UserContext user) => MayWriteAsync(path, StorageProviderCapabilities.Write);

    public async Task<bool> CanDeleteAsync(string path, UserContext user)
        => ToRemote(path).Length > 0 && await MayWriteAsync(path, StorageProviderCapabilities.Delete);

    public async ValueTask DisposeAsync()
    {
        if (_opening is null) return;
        try
        {
            await (await _opening).Session.DisposeAsync();
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // Opening failed and was already reported to the caller; nothing to close.
        }
    }

    private async Task<bool> HasCapabilityAsync(string path, StorageProviderCapabilities capability)
    {
        ToRemote(path);
        return (await OpenAsync()).Session.Capabilities.HasFlag(capability);
    }

    private async Task<bool> MayWriteAsync(string path, StorageProviderCapabilities capability)
    {
        ToRemote(path);
        return mount.Permission == CloudAccessPermission.Write
               && (await OpenAsync()).Session.Capabilities.HasFlag(capability);
    }

    /// <summary>The session for a write; refuses without Write permission or the provider capability.</summary>
    private async Task<(CloudAccessShare Share, IRemoteFileStore Files)> OpenForWriteAsync(
        StorageProviderCapabilities capability)
    {
        if (mount.Permission != CloudAccessPermission.Write)
            throw new UnauthorizedAccessException("This mounted folder is read-only for the user.");
        var (share, session) = await OpenAsync();
        if (!session.Capabilities.HasFlag(capability))
            throw new UnauthorizedAccessException("The storage provider does not support this operation.");
        return (share, session.RemoteFiles!);
    }

    private Task<(CloudAccessShare Share, IStorageSession Session)> OpenAsync()
        => _opening ??= GuardAsync(() => opener.OpenAsync(mount.ShareId));

    /// <summary>Path below the mount root ("" = the root); anything outside the mount is refused.</summary>
    private string ToRemote(string hostPath)
    {
        if (!ShareRelativePath.TryNormalizeStrict(hostPath ?? string.Empty, out var normalized)
            || !ShareRelativePath.IsSameOrDescendant(mount.MountPath, normalized))
            throw new UnauthorizedAccessException("The path is outside the mounted folder.");
        return normalized.Length == mount.MountPath.Length ? string.Empty : normalized[(mount.MountPath.Length + 1)..];
    }

    /// <summary>As <see cref="ToRemote"/>, for operations that must not touch the mount point itself.</summary>
    private string ToRemoteItem(string hostPath)
    {
        var remote = ToRemote(hostPath);
        return remote.Length > 0
            ? remote
            : throw new UnauthorizedAccessException("The mount point itself cannot be changed.");
    }

    private FileMetadata ToHost(FileMetadata item)
    {
        item.Path = ShareRelativePath.Combine(mount.MountPath, item.Path);
        return item;
    }

    private static async Task GuardAsync(Func<Task> action)
        => await GuardAsync(async () => { await action(); return true; });

    /// <summary>Translates provider failures into what the transports already map to status codes.</summary>
    private static async Task<T> GuardAsync<T>(Func<Task<T>> action)
    {
        try
        {
            return await action();
        }
        catch (Exception exception) when (exception is RemoteStorageAccessDeniedException or NotSupportedException)
        {
            throw new UnauthorizedAccessException(exception.Message, exception);
        }
        catch (Exception exception) when (exception is not (IOException or UnauthorizedAccessException or OperationCanceledException))
        {
            throw new RemoteShareUnavailableException("The storage provider could not complete the request.", exception);
        }
    }
}

using Kaimo_File_Server.Core.Services.CloudSync;
using Kaimo_File_Server.Core.Services.ExternalStorage;

namespace Kaimo_File_Server.Infrastructure.ExternalStorage;

/// <summary>
/// Adapts the Package 7 remote-file contract to the existing reconciliation
/// algorithm. This keeps synchronization provider-neutral while that algorithm
/// is moved out of the legacy cloud namespace in a later cleanup package.
/// </summary>
internal sealed class RemoteFileStoreSyncAdapter(
    string serviceName,
    IRemoteFileStore remoteFiles) : ICloudConnection
{
    public string ServiceName { get; } = serviceName;

    public Task Dispose() => Task.CompletedTask;

    public Task UploadAsync(
        string path,
        Stream data,
        DateTime modifiedTime,
        CancellationToken cancellationToken = default)
        => UploadAsync(path, data, modifiedTime, createdTime: null, cancellationToken);

    public async Task UploadAsync(
        string path,
        Stream data,
        DateTime modifiedTime,
        DateTime? createdTime,
        CancellationToken cancellationToken = default)
    {
        await remoteFiles.WriteAsync(path, data, overwrite: true, cancellationToken);
        try
        {
            // Keep the source mtime so the next two-way run does not see the pushed copy
            // as newer, and the creation time so it does not show the upload time.
            // Best-effort: the content is already uploaded.
            await remoteFiles.TrySetTimesAsync(path, modifiedTime, createdTime, cancellationToken);
        }
        catch (Exception) when (!cancellationToken.IsCancellationRequested)
        {
            // ponytail: swallowed silently; the cost is one re-download on the next run.
        }
    }

    // SFTP setstat works on directories as is; WebDAV targets the collection URI. The caller
    // swallows failures.
    public Task TrySetDirectoryModifiedTimeAsync(
        string path, DateTime modifiedUtc, CancellationToken cancellationToken = default)
        => remoteFiles.TrySetDirectoryModifiedTimeAsync(path, modifiedUtc, cancellationToken);

    public Task TrySetDirectoryTimesAsync(
        string path, DateTime modifiedUtc, DateTime? createdUtc, CancellationToken cancellationToken = default)
        => remoteFiles.TrySetDirectoryTimesAsync(path, modifiedUtc, createdUtc, cancellationToken);

    public Task DownloadAsync(
        string path,
        Stream target,
        CancellationToken cancellationToken = default)
        => remoteFiles.DownloadToAsync(path, target, cancellationToken);

    public Task CreateDirectoryAsync(string path, CancellationToken cancellationToken = default)
        => remoteFiles.CreateDirectoryAsync(path, cancellationToken);

    public Task DeleteAsync(string path, bool isDirectory, CancellationToken cancellationToken = default)
        => remoteFiles.DeleteAsync(path, recursive: isDirectory, cancellationToken);

    public async Task<long> GetDirectorySizeAsync(
        string path,
        CancellationToken cancellationToken = default)
    {
        long total = 0;
        foreach (var item in await remoteFiles.ListAsync(path, cancellationToken))
        {
            cancellationToken.ThrowIfCancellationRequested();
            total += item.IsDirectory
                ? await GetDirectorySizeAsync(item.Path, cancellationToken)
                : item.Size ?? 0;
        }
        return total;
    }

    public async Task<IReadOnlyList<CloudItemMeta>> ListAsync(
        string path,
        CancellationToken cancellationToken = default)
        => ToMeta(await remoteFiles.ListAsync(path, cancellationToken));

    public int ListConcurrency => remoteFiles.ListConcurrency;

    public async Task<IReadOnlyDictionary<string, IReadOnlyList<CloudItemMeta>>?> TryListTreeAsync(
        string path,
        CancellationToken cancellationToken = default)
        => await remoteFiles.TryListTreeAsync(path, cancellationToken) is { } tree
            ? tree.ToDictionary(entry => entry.Key, entry => ToMeta(entry.Value), StringComparer.OrdinalIgnoreCase)
            : null;

    private static IReadOnlyList<CloudItemMeta> ToMeta(IEnumerable<RemoteStorageItem> items)
        => items
            .Select(item => new CloudItemMeta(
                item.IsDirectory,
                item.Name,
                item.Path,
                item.Size ?? 0,
                item.ModifiedAtUtc ?? DateTime.UnixEpoch,
                item.CreatedAtUtc))
            .ToArray();
}

using Kaimo_File_Server.Core.Services.DataServices;
using Kaimo_File_Server.Core.Services.ExternalStorage;
using Kaimo_File_Server.Infrastructure.Clouds;

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

    public async Task UploadAsync(
        string path,
        Stream data,
        DateTime modifiedTime,
        CancellationToken cancellationToken = default)
    {
        await remoteFiles.WriteAsync(path, data, overwrite: true, cancellationToken);
        try
        {
            // Keep the source mtime so the next two-way run does not see the pushed copy
            // as newer. Best-effort: the content is already uploaded.
            await remoteFiles.TrySetModifiedTimeAsync(path, modifiedTime, cancellationToken);
        }
        catch (Exception) when (!cancellationToken.IsCancellationRequested)
        {
            // ponytail: swallowed silently; the cost is one re-download on the next run.
        }
    }

    public async Task DownloadAsync(
        string path,
        Stream target,
        CancellationToken cancellationToken = default)
    {
        await using var source = await remoteFiles.OpenReadAsync(path, cancellationToken);
        await source.CopyToAsync(target, cancellationToken);
    }

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
        => (await remoteFiles.ListAsync(path, cancellationToken))
            .Select(item => new CloudItemMeta(
                item.IsDirectory,
                item.Name,
                item.Path,
                item.Size ?? 0,
                item.ModifiedAtUtc ?? DateTime.UnixEpoch))
            .ToArray();
}

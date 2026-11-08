using Kaimo_File_Server.Core.Domain;
using Kaimo_File_Server.Core.Domain.Identity;
using Kaimo_File_Server.Core.Helpers;

namespace Kaimo_File_Server.Web.Services;

public sealed partial class RemoteMountFileService
{
    // ponytail: fixed folder cap per size walk; a partial total beats pinning a request on a huge tree.
    private const int MaxSizedFolders = 20_000;

    private readonly HashSet<string> _refreshed = new(StringComparer.Ordinal);

    public async Task<List<FileMetadata>> ListAsync(string directoryPath, UserContext user)
        => (await ListRemoteAsync(ToRemote(directoryPath))).ConvertAll(ToHost);

    public async Task<FileMetadata?> FindEntryAsync(string path, UserContext user)
    {
        var remote = ToRemote(path);
        if (remote.Length == 0) return MountEntry(mount);
        return await FindRemoteAsync(remote) is { } item ? ToHost(item) : null;
    }

    public async Task<FileMetadata> GetMetadataAsync(string path, UserContext user)
        => await FindEntryAsync(path, user) ?? throw new FileNotFoundException("The item does not exist.");

    /// <summary>
    /// Whether a write target segment exists: the folders leading to the mount always do. A failure
    /// counts as missing, the write that follows reports it properly.
    /// </summary>
    public async Task<bool> ExistsAsync(string path, UserContext user)
    {
        if (ShareRelativePath.IsSameOrDescendant(path, mount.MountPath)) return true;
        try
        {
            return await FindEntryAsync(path, user) is not null;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return false;
        }
    }

    public Task<Stream> ReadFileAsync(string path, UserContext user)
        => ReadRemoteAsync(ToRemoteItem(path));

    public Task<Stream> ReadFileAsync(string path, UserContext user, bool preserveAccessTime)
        => ReadRemoteAsync(ToRemoteItem(path));

    public async Task<long> GetDirectorySizeAsync(string relativePath, UserContext userContext)
    {
        long total = 0;
        var pending = new Queue<string>([ToRemote(relativePath)]);
        for (var visited = 0; visited < MaxSizedFolders && pending.TryDequeue(out var folder); visited++)
        {
            foreach (var item in await ListRemoteAsync(folder))
            {
                if (item.IsDirectory) pending.Enqueue(item.Path);
                else total += item.Size;
            }
        }
        return total;
    }

    public Task<HashSet<string>> FilterReadablePathsAsync(
        IReadOnlyList<(string relativePath, bool isDirectory)> items, UserContext user)
        => Task.FromResult(items.Select(i => i.relativePath).ToHashSet(StringComparer.OrdinalIgnoreCase));

    private async Task<Stream> ReadRemoteAsync(string remote)
    {
        var (share, session) = await OpenAsync();
        return await GuardAsync(() => session.RemoteFiles!.OpenReadAsync(CloudAccessRemoteSessionOpener.RemotePath(share, remote)));
    }

    /// <summary>
    /// A folder listing through the cache shared with the web browser. Providers report a missing
    /// folder differently (a plain IOException on WebDAV, an SSH exception on SFTP), so a failed
    /// listing counts as missing only when the parent listing confirms the folder is not there. An
    /// outage fails that lookup as well and stays the error it was.
    /// </summary>
    private async Task<List<FileMetadata>> ListRemoteAsync(string remote)
    {
        // A change decides on what exists now: its preconditions must see edits made at the provider.
        if (freshListings && _refreshed.Add(remote))
            cache.InvalidatePath(mount.ShareId, remote);

        try
        {
            return await cache.GetOrCreateAsync(mount.ShareId, remote, () => LoadAsync(remote));
        }
        catch (Exception exception) when (
            remote.Length > 0 && exception is not (UnauthorizedAccessException or OperationCanceledException or DirectoryNotFoundException))
        {
            if (await FindRemoteAsync(remote) is not { IsDirectory: true })
                throw new DirectoryNotFoundException("The folder does not exist.");
            throw;
        }
    }

    private Task<List<FileMetadata>> LoadAsync(string remote) => GuardAsync(async () =>
    {
        var (share, session) = await OpenAsync();
        var items = await session.RemoteFiles!.ListAsync(CloudAccessRemoteSessionOpener.RemotePath(share, remote));
        return items.Select(item => CloudAccessRemoteSessionOpener.ToMetadata(share, item)).ToList();
    });

    private async Task<FileMetadata?> FindRemoteAsync(string remote)
    {
        var name = ShareRelativePath.GetFileName(remote);
        try
        {
            return (await ListRemoteAsync(ShareRelativePath.GetParent(remote)))
                .FirstOrDefault(item => string.Equals(item.Name, name, StringComparison.Ordinal));
        }
        catch (DirectoryNotFoundException)
        {
            return null;
        }
    }
}

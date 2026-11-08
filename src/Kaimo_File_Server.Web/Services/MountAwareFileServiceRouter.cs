using Kaimo_File_Server.Core.Domain;
using Kaimo_File_Server.Core.Domain.Identity;
using Kaimo_File_Server.Core.Services.File;

namespace Kaimo_File_Server.Web.Services;

/// <summary>
/// Picks the file service for a path of a local share in REST and WebDAV requests: the adapter of
/// a mounted folder the user may open, otherwise the normal service, so hidden mounts behave
/// exactly as if they were not there. Scoped to one request, and returns one instance per backend,
/// so <see cref="object.ReferenceEquals"/> tells whether two paths share a backend.
/// </summary>
public sealed class MountAwareFileServiceRouter(
    CloudAccessMountResolver resolver,
    IFileServiceFactory fileServiceFactory,
    CloudAccessRemoteSessionOpener opener,
    CloudAccessDirectoryCache cache) : IAsyncDisposable
{
    private readonly Dictionary<Guid, List<MountedFolder>> _mounts = [];
    private readonly Dictionary<Guid, IFileService> _local = [];
    private readonly Dictionary<Guid, RemoteMountFileService> _adapters = [];

    /// <param name="freshListings">
    /// For requests that change something: their preconditions must not be answered from the listing cache.
    /// </param>
    public async Task<IFileService> ForPathAsync(
        ShareDefinition share, string hostPath, UserContext user, bool freshListings = false)
    {
        if (CloudAccessMountResolver.Match(await MountsAsync(share, user), hostPath) is not { } match)
            return Local(share);

        if (!_adapters.TryGetValue(match.Mount.ShareId, out var adapter))
            _adapters[match.Mount.ShareId] = adapter = new RemoteMountFileService(match.Mount, opener, cache, freshListings);
        return adapter;
    }

    /// <summary>The mounts directly in <paramref name="directory"/>, as entries for its listing.</summary>
    public async Task<IReadOnlyList<FileMetadata>> MountEntriesInAsync(ShareDefinition share, string directory, UserContext user)
        => CloudAccessMountResolver.ChildrenOf(await MountsAsync(share, user), directory)
            .Select(RemoteMountFileService.MountEntry)
            .ToList();

    public async ValueTask DisposeAsync()
    {
        foreach (var adapter in _adapters.Values)
            await adapter.DisposeAsync();
    }

    private async Task<List<MountedFolder>> MountsAsync(ShareDefinition share, UserContext user)
    {
        if (!_mounts.TryGetValue(share.Id, out var mounts))
            _mounts[share.Id] = mounts = await resolver.GetVisibleAsync(share, user, includeCanManage: false);
        return mounts;
    }

    private IFileService Local(ShareDefinition share)
    {
        if (!_local.TryGetValue(share.Id, out var service))
            _local[share.Id] = service = fileServiceFactory.CreateForShare(share);
        return service;
    }
}

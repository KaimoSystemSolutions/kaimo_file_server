using Kaimo_File_Server.Core.Domain;
using Kaimo_File_Server.Core.Domain.Identity;
using Kaimo_File_Server.Core.Repositories.ExternalStorage;
using Kaimo_File_Server.Core.Repositories.Shares;
using Kaimo_File_Server.Core.Services.ExternalStorage;
using Kaimo_File_Server.Core.Services.File;

namespace Kaimo_File_Server.Web.Services;

/// <summary>
/// Picks the file service for a path in REST and WebDAV requests: the adapter of a standalone
/// virtual share or of a mounted folder the user may open, otherwise the normal service, so hidden
/// mounts behave exactly as if they were not there. Scoped to one request, and returns one instance
/// per backend, so <see cref="object.ReferenceEquals"/> tells whether two paths share a backend.
/// </summary>
public sealed class MountAwareFileServiceRouter(
    CloudAccessMountResolver resolver,
    IFileServiceFactory fileServiceFactory,
    CloudAccessRemoteSessionOpener opener,
    CloudAccessDirectoryCache cache,
    ICloudAccessRepository repository,
    CloudAccessAuthorizationService authorization,
    IStorageConnectionRepository connections) : IAsyncDisposable
{
    private readonly Dictionary<Guid, List<MountedFolder>> _mounts = [];
    private readonly Dictionary<Guid, IFileService> _local = [];
    private readonly Dictionary<Guid, RemoteMountFileService> _adapters = [];
    private readonly Dictionary<Guid, MountedFolder> _standalone = [];

    /// <param name="freshListings">
    /// For requests that change something: their preconditions must not be answered from the listing cache.
    /// </param>
    public async Task<IFileService> ForPathAsync(
        ShareDefinition share, string hostPath, UserContext user, bool freshListings = false)
    {
        if (_standalone.TryGetValue(share.Id, out var standalone))
            return Adapter(standalone, freshListings);

        return CloudAccessMountResolver.Match(await MountsAsync(share, user), hostPath) is { } match
            ? Adapter(match.Mount, freshListings)
            : Local(share);
    }

    /// <summary>The standalone virtual share with this id if the user may open it, addressable like a share.</summary>
    public async Task<ShareDefinition?> GetVirtualShareAsync(Guid id, UserContext user)
        => await repository.GetShareAsync(id) is { } share ? await AddressableAsync(share, user) : null;

    /// <summary>As <see cref="GetVirtualShareAsync"/>, by exact name.</summary>
    public async Task<ShareDefinition?> GetVirtualShareByNameAsync(string name, UserContext user)
        => (await repository.GetSharesAsync()).FirstOrDefault(s => s.Name == name) is { } share
            ? await AddressableAsync(share, user)
            : null;

    /// <summary>The standalone virtual shares to offer the user: those on a connection that is ready.</summary>
    public async Task<List<ShareDefinition>> VisibleVirtualSharesAsync(UserContext user)
    {
        var ready = (await connections.GetAllAsync())
            .Where(c => c.State == StorageConnectionState.Ready)
            .Select(c => c.Id)
            .ToHashSet();

        var visible = new List<ShareDefinition>();
        foreach (var share in await repository.GetSharesAsync())
            if (ready.Contains(share.ConnectionId) && await AddressableAsync(share, user) is { } addressable)
                visible.Add(addressable);
        return visible;
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

    /// <summary>
    /// A share the controllers can address, or null for a mounted share (reachable only through its
    /// host, so the host ACL applies) and for users without access. It is never stored, and its path
    /// is never used: <see cref="ForPathAsync"/> routes it to the adapter.
    /// </summary>
    private async Task<ShareDefinition?> AddressableAsync(CloudAccessShare share, UserContext user)
    {
        if (share.IsFolderMode || await authorization.GetEffectivePermissionAsync(user, share) is not { } permission)
            return null;

        _standalone[share.Id] = new MountedFolder(share.Id, share.Name, string.Empty) { Permission = permission };
        return new ShareDefinition("virtual-share", "/nonexistent", share.DepartmentId) { Id = share.Id, Name = share.Name };
    }

    private RemoteMountFileService Adapter(MountedFolder mount, bool freshListings)
    {
        if (!_adapters.TryGetValue(mount.ShareId, out var adapter))
            _adapters[mount.ShareId] = adapter = new RemoteMountFileService(mount, opener, cache, freshListings);
        return adapter;
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

using Kaimo_File_Server.Core.Domain;
using Kaimo_File_Server.Core.Domain.Identity;
using Kaimo_File_Server.Core.Helpers;
using Kaimo_File_Server.Core.Services.ExternalStorage;

namespace Kaimo_File_Server.Web.Components.ViewModels;

public partial class CloudAccessViewModel
{
    public async Task LoadPrincipalsAsync(Guid departmentId)
    {
        await EnsureCanManageDepartmentAsync(departmentId);
        var departmentIds = (await _departments.GetDescendantIdsAsync(departmentId)).Append(departmentId).ToHashSet();
        var users = new Dictionary<Guid, User>();
        var groups = new Dictionary<Guid, Group>();
        foreach (var id in departmentIds)
        {
            foreach (var user in await _departments.GetUsersAsync(id)) users[user.Id] = user;
            foreach (var group in await _departments.GetGroupsAsync(id)) groups[group.Id] = group;
        }
        // The creator and the built-in Admins group receive access to every new share
        // (see CreateShareAsync). They must always be visible and manageable in the ACL
        // editor even when they sit outside this department's scope.
        if (_actor is not null) users[_actor.User.Id] = _actor.User;
        foreach (var group in await _groups.GetAllAsync())
            if (string.Equals(group.Name, AdminsGroupName, StringComparison.OrdinalIgnoreCase))
                groups[group.Id] = group;
        AvailablePrincipals = users.Values.Cast<Identity>().Concat(groups.Values)
            .OrderBy(x => x is Group ? 1 : 0).ThenBy(x => x.Name).ToList();
    }

    public async Task CreateShareAsync(
        Guid connectionId,
        string name,
        string remoteRootPath,
        Guid departmentId,
        IReadOnlyDictionary<Guid, CloudAccessPermission> grants)
    {
        await EnsureCanManageDepartmentAsync(departmentId);
        var connectionRecord = await GetUsableConnectionAsync(connectionId);
        if (_providerCatalog is not null
            && !_providerCatalog.GetRequired(connectionRecord.ProviderId).Capabilities
                .HasFlag(StorageProviderCapabilities.Browse))
            throw new NotSupportedException(R("Web_ExternalStorage_VirtualShareUnsupported"));
        if (connectionRecord.State != StorageConnectionState.Ready)
            throw new InvalidOperationException(R("Web_CloudAccess_ConnectionNotReady"));
        var normalizedName = name.Trim();
        if (string.IsNullOrWhiteSpace(normalizedName)
            || normalizedName.Length > 200
            || !SambaName.IsValidShareName(normalizedName))
            throw new ArgumentException(R("Web_CloudAccess_InvalidShareName"));
        if (!await IsShareNameAvailableAsync(normalizedName))
            throw new InvalidOperationException(R("Web_CloudAccess_ShareNameExists"));
        if (!ShareRelativePath.TryNormalizeStrict(remoteRootPath.Trim('/'), out var root))
            throw new UnauthorizedAccessException(R("Web_CloudAccess_InvalidRemotePath"));

        StorageDirectoryTarget folder;
        if (_directoryTargets is not null)
            folder = await _directoryTargets.ResolveDirectoryAsync(connectionRecord, root);
        else
        {
            await using var legacyConnection = _oneDriveConnections.Create(connectionRecord);
            var legacyFolder = await legacyConnection.ResolveFolderAsync(root);
            folder = new StorageDirectoryTarget(NormalizeRemotePath(legacyFolder.Path), legacyFolder.ProviderId);
        }

        var share = new CloudAccessShare
        {
            ConnectionId = connectionId,
            DepartmentId = departmentId,
            Name = normalizedName,
            RemoteRootPath = folder.Path,
            RemoteRootItemId = folder.StableId,
            IsEnabled = true
        };
        await _repository.UpsertShareAsync(share);

        // The creator and the built-in Admins group always get read+write on a new share,
        // regardless of the department-scoped principal picker.
        var effectiveGrants = new Dictionary<Guid, CloudAccessPermission>(grants)
        {
            [_actor!.User.Id] = CloudAccessPermission.Write
        };
        var alwaysAllowed = new HashSet<Guid> { _actor.User.Id };
        if (await ResolveAdminsGroupIdAsync() is { } adminsGroupId)
        {
            effectiveGrants[adminsGroupId] = CloudAccessPermission.Write;
            alwaysAllowed.Add(adminsGroupId);
        }
        await _repository.ReplaceGrantsAsync(
            share.Id, BuildGrants(share.Id, connectionRecord, effectiveGrants, alwaysAllowed));
        await LoadAsync();
    }

    /// <summary>Name of the seeded system group that always receives access to new virtual shares.</summary>
    private const string AdminsGroupName = "Admins";

    private async Task<Guid?> ResolveAdminsGroupIdAsync()
        => (await _groups.GetAllAsync())
            .FirstOrDefault(group => string.Equals(group.Name, AdminsGroupName, StringComparison.OrdinalIgnoreCase))?.Id;

    /// <summary>
    /// Materializes root-level grants for the allowed principals, clamping Write to Read
    /// when the backing provider cannot write (mirrors the old share-wide read-only flag).
    /// Principals in <paramref name="alwaysAllowed"/> bypass the department-scoped filter.
    /// </summary>
    private IEnumerable<CloudAccessGrant> BuildGrants(
        Guid shareId,
        StorageConnection connection,
        IReadOnlyDictionary<Guid, CloudAccessPermission> grants,
        IReadOnlySet<Guid>? alwaysAllowed = null)
    {
        bool canWrite = Supports(connection, StorageProviderCapabilities.Write);
        var allowed = AvailablePrincipals.Select(x => x.Id).ToHashSet();
        return grants
            .Where(pair => allowed.Contains(pair.Key) || (alwaysAllowed?.Contains(pair.Key) ?? false))
            .Select(pair => new CloudAccessGrant
            {
                ShareId = shareId,
                PrincipalId = pair.Key,
                Permission = canWrite ? pair.Value : CloudAccessPermission.Read,
                GrantedByUserId = _actor!.User.Id
            });
    }

    /// <summary>
    /// Updates a virtual share while preserving its identity and existing grants.
    /// The selected remote folder is resolved again to keep the persisted provider item ID current.
    /// </summary>
    public async Task UpdateShareAsync(
        Guid shareId,
        string name,
        string remoteRootPath)
    {
        var share = await _repository.GetShareAsync(shareId)
                    ?? throw new InvalidOperationException(R("Web_CloudAccess_ShareMissing"));
        await EnsureCanManageDepartmentAsync(share.DepartmentId);

        var normalizedName = name.Trim();
        if (string.IsNullOrWhiteSpace(normalizedName)
            || normalizedName.Length > 200
            || !SambaName.IsValidShareName(normalizedName))
            throw new ArgumentException(R("Web_CloudAccess_InvalidShareName"));
        if (!await IsShareNameAvailableAsync(normalizedName, share.Id))
            throw new InvalidOperationException(R("Web_CloudAccess_ShareNameExists"));
        if (!ShareRelativePath.TryNormalizeStrict(remoteRootPath.Trim('/'), out var root))
            throw new UnauthorizedAccessException(R("Web_CloudAccess_InvalidRemotePath"));

        var connectionRecord = await GetUsableConnectionAsync(share.ConnectionId);
        if (connectionRecord.State != StorageConnectionState.Ready)
            throw new InvalidOperationException(R("Web_CloudAccess_ConnectionNotReady"));

        StorageDirectoryTarget folder;
        if (_directoryTargets is not null)
            folder = await _directoryTargets.ResolveDirectoryAsync(connectionRecord, root);
        else
        {
            await using var legacyConnection = _oneDriveConnections.Create(connectionRecord);
            var legacyFolder = await legacyConnection.ResolveFolderAsync(root);
            folder = new StorageDirectoryTarget(NormalizeRemotePath(legacyFolder.Path), legacyFolder.ProviderId);
        }

        share.Name = normalizedName;
        share.RemoteRootPath = folder.Path;
        share.RemoteRootItemId = folder.StableId;
        await _repository.UpsertShareAsync(share);
        await LoadAsync();
    }

    /// <summary>
    /// Reassigns a virtual share to another department. The actor must be able to manage
    /// both the current and the target department. Existing grants are principal-keyed and
    /// untouched; what shifts is which department admins may manage the share (mirrors the
    /// local share department change).
    /// </summary>
    public async Task ChangeShareDepartmentAsync(Guid shareId, Guid departmentId)
    {
        var share = await _repository.GetShareAsync(shareId)
                    ?? throw new InvalidOperationException(R("Web_CloudAccess_ShareMissing"));
        await EnsureCanManageDepartmentAsync(share.DepartmentId);
        await EnsureCanManageDepartmentAsync(departmentId);
        if (share.DepartmentId == departmentId) return;

        share.DepartmentId = departmentId;
        await _repository.UpsertShareAsync(share);
        await LoadAsync();
    }

    public async Task DeleteShareAsync(Guid shareId)
    {
        var share = await _repository.GetShareAsync(shareId)
                    ?? throw new InvalidOperationException(R("Web_CloudAccess_ShareMissing"));
        await EnsureCanManageDepartmentAsync(share.DepartmentId);
        await _repository.DeleteShareAsync(shareId);
        await LoadAsync();
    }

    public async Task<Dictionary<Guid, CloudAccessPermission>> LoadShareGrantsAsync(Guid shareId)
    {
        var share = await _repository.GetShareAsync(shareId)
                    ?? throw new InvalidOperationException(R("Web_CloudAccess_ShareMissing"));
        await EnsureCanManageDepartmentAsync(share.DepartmentId);
        await LoadPrincipalsAsync(share.DepartmentId);
        var allowed = AvailablePrincipals.Select(x => x.Id).ToHashSet();
        return (await _repository.GetGrantsAsync(shareId))
            .Where(grant => allowed.Contains(grant.PrincipalId))
            .ToDictionary(grant => grant.PrincipalId, grant => grant.Permission);
    }

    public async Task UpdateShareGrantsAsync(Guid shareId, IReadOnlyDictionary<Guid, CloudAccessPermission> grants)
    {
        var share = await _repository.GetShareAsync(shareId)
                    ?? throw new InvalidOperationException(R("Web_CloudAccess_ShareMissing"));
        await EnsureCanManageDepartmentAsync(share.DepartmentId);
        await LoadPrincipalsAsync(share.DepartmentId);
        var connectionRecord = await GetUsableConnectionAsync(share.ConnectionId);
        // The editor only manages principals it can display (the department scope). Grants
        // for principals outside that scope — e.g. the creator or the Admins group on a
        // non-global share — are preserved so an ACL edit never silently drops them.
        var allowed = AvailablePrincipals.Select(x => x.Id).ToHashSet();
        var preserved = (await _repository.GetGrantsAsync(shareId))
            .Where(grant => !allowed.Contains(grant.PrincipalId));
        await _repository.ReplaceGrantsAsync(
            shareId, preserved.Concat(BuildGrants(shareId, connectionRecord, grants)));
    }
}

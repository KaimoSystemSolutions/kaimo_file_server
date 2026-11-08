using Kaimo_File_Server.Core.Domain;

namespace Kaimo_File_Server.Core.Repositories;

public interface ICloudAccessRepository
{
    Task<List<CloudAccessShare>> GetSharesAsync(CancellationToken cancellationToken = default);
    /// <summary>The shares mounted as a folder into <paramref name="hostShareId"/>.</summary>
    Task<List<CloudAccessShare>> GetMountedSharesAsync(Guid hostShareId, CancellationToken cancellationToken = default);
    Task<CloudAccessShare?> GetShareAsync(Guid id, CancellationToken cancellationToken = default);
    /// <summary>
    /// Creates or updates a share. On update the provisioning mode and mount point are kept:
    /// they are owned by <see cref="SetMountAsync"/> and the mount guard, so a stale copy held
    /// by an editor never undoes a mount rebase or detach.
    /// </summary>
    Task UpsertShareAsync(CloudAccessShare share, CancellationToken cancellationToken = default);

    /// <summary>Sets the provisioning mode and mount point of a share.</summary>
    Task SetMountAsync(Guid shareId, CloudAccessProvisioning provisioning, Guid? mountShareId, string? mountPath,
        CancellationToken cancellationToken = default);
    Task DeleteShareAsync(Guid id, CancellationToken cancellationToken = default);

    Task<List<CloudAccessGrant>> GetGrantsAsync(Guid shareId, CancellationToken cancellationToken = default);
    Task ReplaceGrantsAsync(Guid shareId, IEnumerable<CloudAccessGrant> grants, CancellationToken cancellationToken = default);
    Task<bool> HasGrantAsync(Guid shareId, IEnumerable<Guid> principalIds, CancellationToken cancellationToken = default);

    /// <summary>
    /// Highest access level granted to any of <paramref name="principalIds"/> on the
    /// share, or <c>null</c> when none of them is granted access. Write outranks Read.
    /// </summary>
    Task<CloudAccessPermission?> GetEffectivePermissionAsync(
        Guid shareId, IEnumerable<Guid> principalIds, CancellationToken cancellationToken = default);
}

namespace Kaimo_File_Server.Core.Services.File;

/// <summary>
/// Keeps virtual-share mount points (a <c>CloudAccessShare</c> in Folder mode) consistent with
/// the local share they live in. Mount points exist only in the database, so the name is
/// reserved against every user write and follows renames of its parent folders.
/// </summary>
public interface ICloudAccessMountGuard
{
    /// <summary>
    /// True when <paramref name="relativePath"/> equals a mount path of the share or lies
    /// below one (case-insensitive). Hidden or disabled mounts count too.
    /// </summary>
    Task<bool> IsReservedAsync(Guid shareId, string relativePath, CancellationToken ct = default);

    /// <summary>
    /// Rebases mounts at or below <paramref name="oldRelativePath"/> onto
    /// <paramref name="newRelativePath"/>. A move into the recycle bin detaches them instead.
    /// Idempotent so durable SMB lifecycle events can be retried.
    /// </summary>
    Task RenamePathAsync(Guid shareId, string oldRelativePath, string newRelativePath, int recycleRootDepth);

    /// <summary>
    /// Detaches mounts at or below <paramref name="relativePath"/>. The virtual share stays in
    /// Folder mode (orphaned) so it is never re-exposed as a standalone share.
    /// </summary>
    Task DetachAsync(Guid shareId, string relativePath);
}

namespace Kaimo_File_Server.Core.Services.File;

/// <summary>
/// Keeps persisted cloud-sync folder mappings aligned with filesystem renames.
/// </summary>
public interface ICloudSyncPathUpdater
{
    /// <summary>
    /// Replaces <paramref name="oldRelativePath"/> with
    /// <paramref name="newRelativePath"/> in every affected local sync path of
    /// the specified share. Implementations must be idempotent so durable SMB
    /// lifecycle events can be retried safely.
    /// </summary>
    Task RenamePathAsync(
        Guid shareId,
        string oldRelativePath,
        string newRelativePath);
}

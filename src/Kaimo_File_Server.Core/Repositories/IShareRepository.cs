using Kaimo_File_Server.Core.Domain;

namespace Kaimo_File_Server.Core.Repositories
{
    /// <summary>
    /// Repository for managing <see cref="ShareDefinition"/> records
    /// (the configuration objects that describe each network share).
    /// </summary>
    public interface IShareRepository
    {
        /// <summary>
        /// Returns all shares that are currently enabled.
        /// Typically used at server startup to register active shares.
        /// </summary>
        Task<List<ShareDefinition>> GetAllEnabledAsync();

        /// <summary>
        /// Returns every share definition regardless of its enabled state.
        /// Used by admin interfaces for full inventory.
        /// </summary>
        Task<List<ShareDefinition>> GetAllAsync();

        /// <summary>
        /// Looks up a share by its unique name (case-insensitive match expected).
        /// </summary>
        Task<ShareDefinition?> GetByNameAsync(string name);

        /// <summary>
        /// Looks up a share by its unique identifier.
        /// </summary>
        Task<ShareDefinition?> GetByIdAsync(Guid id);

        /// <summary>
        /// Creates a new share definition.
        /// </summary>
        /// <returns>The created definition with server-generated fields populated.</returns>
        Task<ShareDefinition> CreateAsync(ShareDefinition share);

        /// <summary>
        /// Updates an existing share definition (name, path, quota, etc.).
        /// </summary>
        Task UpdateAsync(ShareDefinition share);

        /// <summary>
        /// Updates only the share name and storage path against the latest row,
        /// preserving cloud-sync mappings and other concurrently changed fields.
        /// </summary>
        Task UpdateLocationAsync(Guid shareId, string name, string path);

        /// <summary>
        /// Merges runtime-only cloud-sync state into the latest persisted share
        /// aggregate without overwriting concurrent share or mapping edits.
        /// </summary>
        /// <returns>False when the share or local mapping no longer exists.</returns>
        Task<bool> UpdateCloudSyncRuntimeStateAsync(
            Guid shareId,
            string localPath,
            DateTime? lastSync,
            IReadOnlyDictionary<string, string> credentialChanges);

        /// <summary>
        /// Deletes a share definition by its identifier.
        /// Implementations should cascade-delete related access entries and ACLs.
        /// </summary>
        Task DeleteAsync(Guid id);
    }
}

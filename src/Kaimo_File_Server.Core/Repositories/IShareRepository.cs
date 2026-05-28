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
        /// Deletes a share definition by its identifier.
        /// Implementations should cascade-delete related access entries and ACLs.
        /// </summary>
        Task DeleteAsync(Guid id);
    }
}
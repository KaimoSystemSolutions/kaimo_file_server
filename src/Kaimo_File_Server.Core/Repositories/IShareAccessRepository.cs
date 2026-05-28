using Kaimo_File_Server.Core.Domain;

namespace Kaimo_File_Server.Core.Repositories
{
    /// <summary>
    /// Repository for controlling which principals (users / groups) are allowed
    /// to access a share at all. This is the coarse-grained "share gate" —
    /// fine-grained file-level permissions are handled by <see cref="IAclRepository"/>.
    /// </summary>
    public interface IShareAccessRepository
    {
        /// <summary>
        /// Checks whether a principal has been granted access to a share.
        /// </summary>
        /// <param name="shareName">The share's unique name.</param>
        /// <param name="principalId">The user or group identifier.</param>
        Task<bool> HasAccessAsync(string shareName, Guid principalId);

        /// <summary>
        /// Returns every access entry for a share.
        /// </summary>
        Task<List<ShareAccessEntry>> GetByShareAsync(string shareName);

        /// <summary>
        /// Grants a principal access to the specified share.
        /// </summary>
        Task GrantAccessAsync(string shareName, Guid principalId);

        /// <summary>
        /// Revokes a principal's access to the specified share.
        /// </summary>
        Task RevokeAccessAsync(string shareName, Guid principalId);

        /// <summary>
        /// Updates all access entries when a share is renamed.
        /// </summary>
        /// <param name="oldName">The previous share name.</param>
        /// <param name="newName">The new share name.</param>
        Task UpdateShareNameAsync(string oldName, string newName);

        /// <summary>
        /// Ensures the root <see cref="FileMetadata"/> record (Path = "")
        /// exists for a share and that the owner has an initial FullControl ACL
        /// entry with full inheritance enabled.
        /// This method is idempotent — calling it again is a no-op.
        /// </summary>
        /// <param name="shareId">The share's unique identifier.</param>
        /// <param name="ownerId">The user who should own the root ACL entry.</param>
        Task EnsureShareRootAclAsync(Guid shareId, Guid ownerId);
    }
}
using Kaimo_File_Server.Core.Services.Sync;

namespace Kaimo_File_Server.Core.Repositories
{
    /// <summary>
    /// Reads a cheap change fingerprint for a share subtree from the persisted
    /// <c>file_metadata</c> projection. Backs the client API's long-poll change
    /// notification without requiring a dedicated change-log table.
    /// </summary>
    public interface IFileChangeCursorRepository
    {
        /// <summary>
        /// Returns the current <see cref="ShareChangeState"/> for the given share,
        /// optionally restricted to a share-relative subtree.
        /// </summary>
        /// <param name="shareId">The share to inspect.</param>
        /// <param name="pathPrefix">
        /// Normalized share-relative prefix (forward slashes, no leading/trailing
        /// slash). <c>null</c> or empty covers the whole share.
        /// </param>
        Task<ShareChangeState> GetShareChangeStateAsync(
            Guid shareId, string? pathPrefix, CancellationToken ct = default);
    }
}

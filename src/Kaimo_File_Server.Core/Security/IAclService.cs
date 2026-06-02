using Kaimo_File_Server.Core.Domain.Identity;

namespace Kaimo_File_Server.Core.Security
{
    public interface IAclService
    {
        Task<bool> HasAccessAsync(UserContext userContext, Guid shareId,
            string relativePath, bool isDirectory, FilePermission permission);

        Task RenameAclPathAsync(Guid shareId, string oldRelativePath, string newRelativePath);
        Task DeleteAclAsync(Guid shareId, string relativePath);

        /// <summary>
        /// Batch access check — evaluates permission for multiple items in a single
        /// DB round trip. All hierarchy paths are de-duplicated and fetched once,
        /// then each item's effective ACL is resolved in-memory.
        ///
        /// Returns a dictionary mapping each normalized path → access result.
        ///
        /// Performance: O(1) DB queries regardless of item count.
        /// A directory with 1000 files produces 1 query, not 1000.
        /// </summary>
        Task<Dictionary<string, bool>> HasAccessBatchAsync(
            UserContext userContext, Guid shareId,
            IReadOnlyList<(string relativePath, bool isDirectory)> items,
            FilePermission permission);
    }
}
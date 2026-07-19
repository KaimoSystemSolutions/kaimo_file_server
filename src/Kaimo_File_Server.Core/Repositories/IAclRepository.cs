using Kaimo_File_Server.Core.Security;

namespace Kaimo_File_Server.Core.Repositories
{
    /// <summary>
    /// Repository for managing Access Control List (ACL) entries
    /// on file and directory metadata objects.
    /// </summary>
    public interface IAclRepository
    {
        /// <summary>
        /// Retrieves all ACL entries associated with a specific file metadata record.
        /// </summary>
        /// <param name="fileMetadataId">The unique identifier of the file metadata record.</param>
        /// <returns>A list of access entries; empty if none are defined.</returns>
        Task<List<AccessEntry>> GetByFileMetadataIdAsync(Guid fileMetadataId);

        /// <summary>
        /// Persists a new ACL entry.
        /// </summary>
        /// <param name="entry">The access entry to create.</param>
        /// <returns>The created entry with any server-generated fields populated.</returns>
        Task<AccessEntry> AddAsync(AccessEntry entry);

        /// <summary>
        /// Updates an existing ACL entry (e.g. changed permission flags).
        /// </summary>
        /// <param name="entry">The modified access entry. <see cref="AccessEntry.Id"/> must be set.</param>
        Task UpdateAsync(AccessEntry entry);

        /// <summary>
        /// Removes a single ACL entry.
        /// </summary>
        /// <param name="entryId">The unique identifier of the entry to delete.</param>
        Task DeleteAsync(Guid entryId);

        /// <summary>
        /// Loads ACL entries for multiple paths within a single share.
        /// Used during inheritance resolution to walk the directory tree upward
        /// and accumulate effective permissions.
        /// </summary>
        /// <param name="shareId">The share that owns the paths.</param>
        /// <param name="paths">
        /// Ordered list of ancestor paths, typically from root ("") down to the target.
        /// </param>
        /// <returns>
        /// A tuple per path containing the relative path, whether it is a directory,
        /// and the list of ACL entries defined directly on that path.
        /// </returns>
        Task<List<(string Path, bool IsDirectory, List<AccessEntry> Acl)>> GetAclsForPathsAsync(
            Guid shareId, List<string> paths);

        /// <summary>
        /// Returns the number of explicit ACL entries per path.
        /// Useful for UI indicators (e.g. showing a badge when custom ACLs exist).
        /// </summary>
        /// <param name="shareId">The share that owns the paths.</param>
        /// <param name="paths">The set of relative paths to count ACLs for.</param>
        /// <returns>A dictionary mapping each path to its ACL entry count.</returns>
        Task<Dictionary<string, int>> GetAclCountsByPathAsync(Guid shareId, IEnumerable<string> paths);

        /// <summary>
        /// Renames all file metadata paths that start with <paramref name="oldRelativePath"/>
        /// to begin with <paramref name="newRelativePath"/> instead.
        /// Called when a file or directory is moved or renamed to keep ACLs consistent.
        /// </summary>
        /// <param name="shareId">The share containing the paths.</param>
        /// <param name="oldRelativePath">The current relative path prefix.</param>
        /// <param name="newRelativePath">The replacement path prefix.</param>
        Task RenameFileMetadataPathsAsync(Guid shareId, string oldRelativePath, string newRelativePath);

        /// <summary>
        /// Removes metadata at a path and every descendant. ACL rows are removed by
        /// the database cascade from file_metadata.
        /// </summary>
        Task<int> DeleteFileMetadataPathsAsync(Guid shareId, string relativePath);

        /// <summary>Removes all file metadata (and cascading ACL rows) for a share.</summary>
        Task<int> DeleteShareFileMetadataAsync(Guid shareId);
    }
}

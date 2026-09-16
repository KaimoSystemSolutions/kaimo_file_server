using Kaimo_File_Server.Core.Domain;

namespace Kaimo_File_Server.Core.Repositories
{
    /// <summary>
    /// Repository for <see cref="FileMetadata"/> records that act as the anchor
    /// for ACL entries, versioning, and other per-path metadata.
    /// </summary>
    public interface IFileMetadataRepository
    {
        /// <summary>
        /// Returns the existing metadata record for <paramref name="path"/>,
        /// or atomically creates one if it does not yet exist.
        /// </summary>
        /// <param name="path">Relative path inside the share.</param>
        /// <param name="isDirectory">Whether the path represents a directory.</param>
        /// <param name="userId">The user creating the record (set as initial owner).</param>
        /// <param name="shareId">The share the path belongs to.</param>
        /// <returns>The existing or newly created metadata record.</returns>
        Task<FileMetadata> GetOrCreateAsync(string path, bool isDirectory, Guid userId, Guid shareId);
    }
}
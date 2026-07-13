namespace Kaimo_File_Server.Core.Services.File;

/// <summary>
/// Records the creating user as the owner of a newly created file or directory.
///
/// The filesystem is the source of truth for browsing and carries no identity, so
/// ownership is persisted separately as a <c>FileMetadata</c> row. The owner is set
/// exactly once — when the row is first created — so the original creator is preserved
/// even if the item is later modified by someone else. Implementations are no-ops when
/// a row already exists.
///
/// Called from <see cref="FileService"/> at every creation choke point, which both the
/// web upload path and the SMB transport funnel through.
/// </summary>
public interface IFileOwnershipService
{
    /// <summary>
    /// Ensures a metadata row exists for <paramref name="shareRelativePath"/> in
    /// <paramref name="shareId"/>, stamping <paramref name="ownerUserId"/> as the owner
    /// when the row is newly created. Never overwrites an existing owner.
    /// </summary>
    Task EnsureOwnerAsync(Guid shareId, string shareRelativePath, bool isDirectory, Guid ownerUserId);
}

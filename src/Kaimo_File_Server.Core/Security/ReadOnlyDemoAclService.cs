using Kaimo_File_Server.Core.Domain.Identity;

namespace Kaimo_File_Server.Core.Security;

/// <summary>
/// Read-only demo guard for <see cref="IAclService"/>. Wraps the real service and
/// denies every permission that carries a non-read bit, so no file/folder can be
/// created, written, renamed, deleted or re-ACL'd. Pure reads pass straight through.
///
/// Registered only when <c>KAIMO_DEMO_READONLY=true</c>; otherwise this type never
/// enters the container and the normal path is untouched. Pairs with the EF
/// SaveChanges interceptor, which blocks all DB + settings writes.
/// </summary>
public sealed class ReadOnlyDemoAclService : IAclService
{
    private readonly IAclService _inner;

    public ReadOnlyDemoAclService(IAclService inner) => _inner = inner;

    /// <summary>True when the mask requests anything beyond the pure read bits.</summary>
    private static bool IsWrite(FilePermission permission)
        => (permission & ~FilePermission.ReadAll) != 0;

    public Task<bool> HasAccessAsync(UserContext userContext, Guid shareId,
        string relativePath, bool isDirectory, FilePermission permission)
        => IsWrite(permission)
            ? Task.FromResult(false)
            : _inner.HasAccessAsync(userContext, shareId, relativePath, isDirectory, permission);

    public Task<Dictionary<string, bool>> HasAccessBatchAsync(
        UserContext userContext, Guid shareId,
        IReadOnlyList<(string relativePath, bool isDirectory)> items,
        FilePermission permission)
        => IsWrite(permission)
            ? Task.FromResult(items.ToDictionary(i => i.relativePath, _ => false))
            : _inner.HasAccessBatchAsync(userContext, shareId, items, permission);

    // ACL-record mutations are only reached after a write was already authorized,
    // so in read-only mode they must never run. Fail loudly if one ever does.
    public Task RenameAclPathAsync(Guid shareId, string oldRelativePath, string newRelativePath)
        => throw new ReadOnlyDemoException();

    public Task DeleteAclAsync(Guid shareId, string relativePath)
        => throw new ReadOnlyDemoException();

    public Task DeleteShareMetadataAsync(Guid shareId)
        => throw new ReadOnlyDemoException();
}

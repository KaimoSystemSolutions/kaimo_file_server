namespace Kaimo_File_Server.Core.Services.File;

/// <summary>
/// Resolves where version blobs of a share are stored. Versions always live in the
/// same storage pool as the share (outside every share folder), never on the
/// application-data volume, so a large write can only fill the pool it targets.
/// </summary>
public interface IVersionStorageLocator
{
    /// <summary>
    /// The version-blob root inside the pool that holds the share, or <c>null</c> when
    /// the share is unknown or not located inside a configured storage pool. A
    /// <c>null</c> result means "do not store versions" — there is no fallback location.
    /// </summary>
    Task<string?> GetRootAsync(Guid shareId);

    /// <summary>The version-blob roots of all configured pools (they may not exist yet).</summary>
    IReadOnlyList<string> AllRoots { get; }
}

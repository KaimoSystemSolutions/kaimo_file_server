using Kaimo_File_Server.Core.Security;

public interface IAclRepository
{
    Task<List<AccessEntry>> GetByFileMetadataIdAsync(Guid fileMetadataId);
    Task<AccessEntry> AddAsync(AccessEntry entry);
    Task UpdateAsync(AccessEntry entry);
    Task DeleteAsync(Guid entryId);

    /// <summary>
    /// Lädt alle ACL-Einträge für die gegebenen Pfade innerhalb eines Shares.
    /// Wird für die Vererbungsauflösung benötigt.
    /// </summary>
    Task<List<(string Path, bool IsDirectory, List<AccessEntry> Acl)>> GetAclsForPathsAsync(
        Guid shareId, List<string> paths);

    Task<Dictionary<string, int>> GetAclCountsByPathAsync(Guid shareId, IEnumerable<string> paths);
}
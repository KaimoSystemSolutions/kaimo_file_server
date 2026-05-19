using Kaimo_File_Server.Core.Domain;

public interface IShareAccessRepository
{
    Task<bool> HasAccessAsync(string shareName, Guid principalId);
    Task<List<ShareAccessEntry>> GetByShareAsync(string shareName);
    Task GrantAccessAsync(string shareName, Guid principalId);
    Task RevokeAccessAsync(string shareName, Guid principalId);
    Task UpdateShareNameAsync(string oldName, string newName);

    /// <summary>
    /// Erstellt den Root-FileMetadata-Eintrag (Path="") für einen Share
    /// und setzt die initiale Owner-ACL mit FullControl + Everything-Vererbung.
    /// Idempotent — wenn der Eintrag schon existiert, passiert nichts.
    /// </summary>
    Task EnsureShareRootAclAsync(Guid shareId, Guid ownerId);
}
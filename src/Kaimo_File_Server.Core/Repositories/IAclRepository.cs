using Kaimo_File_Server.Core.Security;

namespace Kaimo_File_Server.Core.Repositories
{
    public interface IAclRepository
    {
        Task<List<AccessEntry>> GetByFileMetadataIdAsync(Guid fileMetadataId);
        Task<AccessEntry> AddAsync(AccessEntry entry);
        Task UpdateAsync(AccessEntry entry);
        Task DeleteAsync(Guid entryId);
    }
}
using Kaimo_File_Server.Core.Domain;

namespace Kaimo_File_Server.Core.Repositories
{
    public interface IShareRepository
    {
        Task<List<ShareDefinition>> GetAllEnabledAsync();
        Task<List<ShareDefinition>> GetAllAsync();
        Task<ShareDefinition?> GetByNameAsync(string name);
        Task<ShareDefinition?> GetByIdAsync(Guid id);
        Task<ShareDefinition> CreateAsync(ShareDefinition share);
        Task UpdateAsync(ShareDefinition share);
        Task DeleteAsync(Guid id);
    }
}
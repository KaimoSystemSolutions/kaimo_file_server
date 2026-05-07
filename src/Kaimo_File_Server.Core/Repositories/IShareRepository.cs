using Kaimo_File_Server.Core.Domain;
using System;
using System.Collections.Generic;
using System.Text;

namespace Kaimo_File_Server.Core.Repositories
{
    public interface IShareRepository
    {
        Task<List<ShareDefinition>> GetAllEnabledAsync();
        Task<ShareDefinition?> GetByNameAsync(string name);
        Task<ShareDefinition> CreateAsync(ShareDefinition share);
        Task DeleteAsync(Guid id);
    }
}

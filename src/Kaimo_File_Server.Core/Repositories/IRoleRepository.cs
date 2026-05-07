using Kaimo_File_Server.Core.Domain.Identity;
using System;
using System.Collections.Generic;
using System.Text;

namespace Kaimo_File_Server.Core.Repositories
{
    public interface IRoleRepository
    {
        Task<Role?> GetByIdAsync(Guid id);
        Task<IEnumerable<Role>> GetAllAsync();
        Task<Role> CreateAsync(Role role);
        Task DeleteAsync(Guid id);
    }
}

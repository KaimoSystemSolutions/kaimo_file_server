using Kaimo_File_Server_Core.Core.Domain.Identity;
using System;
using System.Collections.Generic;
using System.Text;

namespace Kaimo_File_Server_Core.Core.Repositories
{
    public interface IGroupRepository
    {
        Task<Group?> GetByIdAsync(Guid id);
        Task<IEnumerable<Group>> GetAllAsync();
        Task<Group> CreateAsync(Group group);
        Task DeleteAsync(Guid id);
    }
}

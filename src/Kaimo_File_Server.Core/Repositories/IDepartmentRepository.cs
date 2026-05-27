using Kaimo_File_Server.Core.Domain;
using Kaimo_File_Server.Core.Domain.Department;
using Kaimo_File_Server.Core.Domain.Identity;

namespace Kaimo_File_Server.Core.Repositories
{
    public interface IDepartmentRepository
    {
        Task<Department?> GetByIdAsync(Guid id);
        Task<Department?> GetByNameAsync(string name);
        Task<List<Department>> GetAllAsync();
        Task<List<Department>> GetChildrenAsync(Guid parentId);
        Task<Department> CreateAsync(Department department);
        Task UpdateAsync(Department department);
        Task DeleteAsync(Guid id);

        // ── User ↔ Department ──
        Task<List<User>> GetUsersAsync(Guid departmentId);
        Task<List<Department>> GetDepartmentsForUserAsync(Guid userId);
        Task AddUserAsync(Guid departmentId, Guid userId);
        Task RemoveUserAsync(Guid departmentId, Guid userId);
        Task<bool> IsUserInDepartmentAsync(Guid userId, Guid departmentId);

        // ── Group ↔ Department ──
        Task<List<Group>> GetGroupsAsync(Guid departmentId);
        Task<List<Department>> GetDepartmentsForGroupAsync(Guid groupId);
        Task AddGroupAsync(Guid departmentId, Guid groupId);
        Task RemoveGroupAsync(Guid departmentId, Guid groupId);
        Task<bool> IsGroupInDepartmentAsync(Guid groupId, Guid departmentId);

        // ── Share ↔ Department ──
        Task<List<ShareDefinition>> GetSharesAsync(Guid departmentId);
        Task<List<Department>> GetDepartmentsForShareAsync(Guid shareId);
        Task AddShareAsync(Guid departmentId, Guid shareId);
        Task RemoveShareAsync(Guid departmentId, Guid shareId);
        Task<bool> IsShareInDepartmentAsync(Guid shareId, Guid departmentId);
    }
}
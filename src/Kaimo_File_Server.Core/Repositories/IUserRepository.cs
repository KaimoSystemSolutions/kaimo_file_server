using Kaimo_File_Server.Core.Domain.Identity;

namespace Kaimo_File_Server.Core.Repositories
{
    public interface IUserRepository
    {
        Task<User?> GetByIdAsync(Guid id);
        Task<User?> GetByUsernameAsync(string username);
        Task<IEnumerable<User>> GetAllAsync();
        Task<User> CreateAsync(User user);
        Task UpdateAsync(User user);
        Task DeleteAsync(Guid id);
        Task<List<Group>> GetGroupsForUserAsync(Guid userId);
        Task<List<Role>> GetRolesForUserAsync(Guid userId);
        Task SetGroupsForUserAsync(Guid userId, List<Guid> groupIds);
        Task SetRolesForUserAsync(Guid userId, List<Guid> roleIds);
        Task UpdateNameAsync(Guid userId, string newName);
        Task UpdatePasswordAsync(Guid userId, string passwordHash, string ntHash);
        Task UpdateProfileAsync(Guid userId, string description, string email, bool isEnabled, bool canChangePassword);
    }
}

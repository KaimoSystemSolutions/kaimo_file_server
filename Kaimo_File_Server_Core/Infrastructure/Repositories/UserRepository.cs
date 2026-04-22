using Microsoft.EntityFrameworkCore;
using Kaimo_File_Server_Core.Core.Domain.Identity;
using Kaimo_File_Server_Core.Core.Repositories;
using Kaimo_File_Server_Core.Infrastructure.Persistence;

namespace Kaimo_File_Server_Core.Infrastructure.Repositories
{
    public class UserRepository : IUserRepository
    {
        private readonly ApplicationDbContext _db;

        public UserRepository(ApplicationDbContext db)
        {
            _db = db;
        }

        public async Task<User?> GetByIdAsync(Guid id)
            => await _db.Users.FindAsync(id);

        public async Task<User?> GetByUsernameAsync(string username)
            => await _db.Users.FirstOrDefaultAsync(u => u.Username == username);

        public async Task<IEnumerable<User>> GetAllAsync()
            => await _db.Users.ToListAsync();

        public async Task<User> CreateAsync(User user)
        {
            _db.Users.Add(user);
            await _db.SaveChangesAsync();
            return user;
        }

        public async Task UpdateAsync(User user)
        {
            _db.Users.Update(user);
            await _db.SaveChangesAsync();
        }

        public async Task DeleteAsync(Guid id)
        {
            var user = await _db.Users.FindAsync(id);
            if (user != null)
            {
                _db.Users.Remove(user);
                await _db.SaveChangesAsync();
            }
        }
    }
}

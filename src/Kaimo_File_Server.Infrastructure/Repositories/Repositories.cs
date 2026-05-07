using Kaimo_File_Server.Core.Domain;
using Kaimo_File_Server.Core.Domain.Identity;
using Kaimo_File_Server.Core.Repositories;
using Kaimo_File_Server.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Kaimo_File_Server.Infrastructure.Repositories
{
    public class UserRepository : IUserRepository
    {
        private readonly ApplicationDbContext _db;
        public UserRepository(ApplicationDbContext db) { _db = db; }

        public async Task<User?> GetByIdAsync(Guid id) => await _db.Users.FindAsync(id);
        public async Task<User?> GetByUsernameAsync(string username)
            => await _db.Users.FirstOrDefaultAsync(u => u.Username == username);
        public async Task<IEnumerable<User>> GetAllAsync() => await _db.Users.ToListAsync();
        public async Task<User> CreateAsync(User user) { _db.Users.Add(user); await _db.SaveChangesAsync(); return user; }
        public async Task UpdateAsync(User user) { _db.Users.Update(user); await _db.SaveChangesAsync(); }
        public async Task DeleteAsync(Guid id)
        {
            var user = await _db.Users.FindAsync(id);
            if (user != null) { _db.Users.Remove(user); await _db.SaveChangesAsync(); }
        }
    }

    public class ShareRepository : IShareRepository
    {
        private readonly ApplicationDbContext _db;
        public ShareRepository(ApplicationDbContext db) { _db = db; }

        public async Task<List<ShareDefinition>> GetAllEnabledAsync()
            => await _db.ShareDefinitions.Where(s => s.IsEnabled).ToListAsync();
        public async Task<ShareDefinition?> GetByNameAsync(string name)
            => await _db.ShareDefinitions.FirstOrDefaultAsync(s => s.Name == name);
        public async Task<ShareDefinition> CreateAsync(ShareDefinition share)
        { _db.ShareDefinitions.Add(share); await _db.SaveChangesAsync(); return share; }
        public async Task DeleteAsync(Guid id)
        {
            var share = await _db.ShareDefinitions.FindAsync(id);
            if (share != null) { _db.ShareDefinitions.Remove(share); await _db.SaveChangesAsync(); }
        }
    }

    public class ShareAccessRepository : IShareAccessRepository
    {
        private readonly ApplicationDbContext _db;
        public ShareAccessRepository(ApplicationDbContext db) { _db = db; }

        public async Task<bool> HasAccessAsync(string shareName, Guid principalId)
            => await _db.ShareAccessEntries.AnyAsync(e => e.ShareName == shareName && e.PrincipalId == principalId);
        public async Task<List<ShareAccessEntry>> GetByShareAsync(string shareName)
            => await _db.ShareAccessEntries.Where(e => e.ShareName == shareName).ToListAsync();
    }
}

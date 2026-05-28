using Kaimo_File_Server.Core.Domain;
using Kaimo_File_Server.Core.Domain.Identity;
using Kaimo_File_Server.Core.Repositories;
using Kaimo_File_Server.Core.Security;
using Kaimo_File_Server.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Kaimo_File_Server.Infrastructure.Repositories
{

    public class ShareAccessRepository : IShareAccessRepository
    {
        private readonly ApplicationDbContext _db;
        public ShareAccessRepository(ApplicationDbContext db) { _db = db; }

        /// <summary>
        /// Checks if the user or any of their groups has access to the share. This is used for authorization checks when accessing shares.
        /// </summary>
        /// <param name="shareName">The name of the share.</param>
        /// <param name="principalId">The ID of the user</param>
        /// <returns>True if the user or any of their groups has access to the share; otherwise, false.</returns>
        public async Task<bool> HasAccessAsync(string shareName, Guid principalId)
        {   
            List<Guid> groupsOfUser = await _db.UserGroups
                .Where(userGroup => userGroup.UserId == principalId)
                .Select(userGroup => userGroup.GroupId)
                .ToListAsync();


            return await _db.ShareAccessEntries.AnyAsync((ShareAccessEntry entry) =>
                entry.ShareName == shareName &&
                (entry.PrincipalId == principalId ||
                 groupsOfUser.Contains(entry.PrincipalId))
                );
        }

        public async Task<List<ShareAccessEntry>> GetByShareAsync(string shareName)
            => await _db.ShareAccessEntries.Where(e => e.ShareName == shareName).ToListAsync();

        public async Task GrantAccessAsync(string shareName, Guid principalId)
        {
            var exists = await _db.ShareAccessEntries
                .AnyAsync(e => e.ShareName == shareName && e.PrincipalId == principalId);

            if (!exists)
            {
                _db.ShareAccessEntries.Add(new ShareAccessEntry(shareName, principalId));
                await _db.SaveChangesAsync();
            }
        }

        public async Task RevokeAccessAsync(string shareName, Guid principalId)
        {
            var entry = await _db.ShareAccessEntries
                .FirstOrDefaultAsync(e => e.ShareName == shareName && e.PrincipalId == principalId);

            if (entry != null)
            {
                _db.ShareAccessEntries.Remove(entry);
                await _db.SaveChangesAsync();
            }
        }

        public async Task UpdateShareNameAsync(string oldName, string newName)
        {
            await _db.ShareAccessEntries
                .Where(e => e.ShareName == oldName)
                .ExecuteUpdateAsync(e => e.SetProperty(x => x.ShareName, newName));
        }

        public async Task EnsureShareRootAclAsync(Guid shareId, Guid ownerId)
        {
            // Prüfen ob Root-FileMetadata schon existiert
            var rootMeta = await _db.FileMetadata
                .Include(m => m.Acl)
                .FirstOrDefaultAsync(m => m.ShareId == shareId && m.Path == "");

            if (rootMeta != null)
                return; // Schon vorhanden, nichts tun

            // Root-FileMetadata anlegen
            rootMeta = new FileMetadata
            {
                Id = Guid.NewGuid(),
                ShareId = shareId,
                OwnerId = ownerId,
                Path = "",
                Name = "(root)",
                Size = 0,
                IsDirectory = true,
                CreatedAt = DateTime.UtcNow,
                ModifiedAt = DateTime.UtcNow
            };

            _db.FileMetadata.Add(rootMeta);
            await _db.SaveChangesAsync();

            // Initiale ACL: Owner bekommt FullControl mit Vererbung auf alles
            var ownerAcl = new AccessEntry(
                ownerId,
                AclEntryType.Allow,
                FilePermission.FullControl,
                AclInheritance.Everything)
            {
                FileMetadataId = rootMeta.Id
            };

            _db.AccessEntries.Add(ownerAcl);
            await _db.SaveChangesAsync();
        }
    }
}

using Kaimo_File_Server.Core.Domain;
using Kaimo_File_Server.Core.Helpers;
using Kaimo_File_Server.Core.Repositories;
using Kaimo_File_Server.Core.Security;
using Kaimo_File_Server.Infrastructure.Clouds;
using Kaimo_File_Server.Infrastructure.Persistence;
using Kaimo_File_Server.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;

namespace Kaimo_File_Server.Infrastructure.Repositories;

    /// <summary>
    /// EF Core implementation of <see cref="IShareRepository"/>.
    /// </summary>
    public class ShareRepository : IShareRepository
    {
        private readonly IDbContextFactory<ApplicationDbContext> _dbFactory;
        private readonly ICloudProviderFactory? _cloudFactory;

        public ShareRepository(IDbContextFactory<ApplicationDbContext> dbFactory,  ICloudProviderFactory? cloudFactory)
        {
            _dbFactory = dbFactory;
            _cloudFactory = cloudFactory;
        }

        public async Task<List<ShareDefinition>> GetAllEnabledAsync()
        {
            await using var db = await _dbFactory.CreateDbContextAsync();
            var shares = await db.ShareDefinitions.Where(s => s.IsEnabled).ToListAsync();
            return shares.Where(s => VolumeMountManager.IsPathAccessible(s.Path)).ToList();
        }

        public async Task<List<ShareDefinition>> GetAllAsync()
        {
            await using var db = await _dbFactory.CreateDbContextAsync();
            
            List<ShareDefinition> shares = await db.ShareDefinitions.ToListAsync();
            
            if (_cloudFactory is null) 
                return shares;
            
            // establish missing cloud connections
            //foreach (ShareDefinition share in shares) 
            //     ICloudProviderFactory.initilizeCloud(_cloudFactory, share);

            return shares;
        }

        public async Task<ShareDefinition?> GetByNameAsync(string name)
        {
            await using var db = await _dbFactory.CreateDbContextAsync();
            return await db.ShareDefinitions.FirstOrDefaultAsync(s => s.Name == name);
        }

        public async Task<ShareDefinition?> GetByIdAsync(Guid id)
        {
            await using var db = await _dbFactory.CreateDbContextAsync();
            return await db.ShareDefinitions.FindAsync(id);
        }

        public async Task<ShareDefinition> CreateAsync(ShareDefinition share)
        {
            SambaName.EnsureValidShareName(share.Name, nameof(share));
            await using var db = await _dbFactory.CreateDbContextAsync();
            db.ShareDefinitions.Add(share);
            await db.SaveChangesAsync();
            return share;
        }

        public async Task UpdateAsync(ShareDefinition share)
        {
            SambaName.EnsureValidShareName(share.Name, nameof(share));
            await using var db = await _dbFactory.CreateDbContextAsync();
            db.ShareDefinitions.Update(share);
            await db.SaveChangesAsync();
        }

        public async Task UpdateLocationAsync(
            Guid shareId,
            string name,
            string path)
        {
            SambaName.EnsureValidShareName(name, nameof(name));
            await using var db = await _dbFactory.CreateDbContextAsync();
            var share = await db.ShareDefinitions.FindAsync(shareId)
                ?? throw new InvalidOperationException(
                    $"Share '{shareId}' no longer exists.");
            share.Name = name;
            share.Path = path;
            await db.SaveChangesAsync();
        }

        public async Task<bool> UpdateCloudSyncRuntimeStateAsync(
            Guid shareId,
            string localPath,
            DateTime? lastSync,
            IReadOnlyDictionary<string, string> credentialChanges)
        {
            string normalizedPath = CloudSyncPaths.Normalize(localPath);
            await using var db = await _dbFactory.CreateDbContextAsync();
            var share = await db.ShareDefinitions.FindAsync(shareId);
            if (share?.CloudSettings?.Folders is not { } folders)
                return false;

            string? persistedKey = folders.Keys.FirstOrDefault(key =>
                string.Equals(
                    CloudSyncPaths.Normalize(key), normalizedPath,
                    StringComparison.OrdinalIgnoreCase));
            if (persistedKey is null ||
                !folders.TryGetValue(persistedKey, out var folder))
                return false;

            foreach (var (key, value) in credentialChanges)
                folder.Data[key] = value;
            if (lastSync.HasValue)
                folder.LastSync = lastSync.Value;

            // Force EF change detection for the JSON-converted aggregate while
            // preserving every field freshly loaded in this context.
            share.CloudSettings = new CloudSettings(
                new Dictionary<string, SyncedFolder>(folders));
            await db.SaveChangesAsync();
            return true;
        }

        public async Task DeleteAsync(Guid id)
        {
            await using var db = await _dbFactory.CreateDbContextAsync();
            var share = await db.ShareDefinitions.FindAsync(id);
            if (share != null)
            {
                var shareAssignments = await db.ScopedRoleAssignments
                    .Where(a => a.ScopeType == Kaimo_File_Server.Core.Security.ScopeType.Share
                                && a.ScopeId == id)
                    .ToListAsync();
                db.ScopedRoleAssignments.RemoveRange(shareAssignments);
                db.ShareDefinitions.Remove(share);
                await db.SaveChangesAsync();
            }
        }
    }

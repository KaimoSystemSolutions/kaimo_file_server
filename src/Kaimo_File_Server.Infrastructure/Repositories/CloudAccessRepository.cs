using Kaimo_File_Server.Core.Domain;
using Kaimo_File_Server.Core.Repositories;
using Kaimo_File_Server.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Kaimo_File_Server.Infrastructure.Repositories;

public sealed class CloudAccessRepository(IDbContextFactory<ApplicationDbContext> dbFactory)
    : ICloudAccessRepository
{
    public async Task<List<CloudAccessShare>> GetSharesAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        return await db.CloudAccessShares.AsNoTracking().OrderBy(x => x.Name).ToListAsync(cancellationToken);
    }

    public async Task<CloudAccessShare?> GetShareAsync(Guid id, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        return await db.CloudAccessShares.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id, cancellationToken);
    }

    public async Task UpsertShareAsync(CloudAccessShare share, CancellationToken cancellationToken = default)
    {
        share.UpdatedAtUtc = DateTime.UtcNow;
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        if (await db.CloudAccessShares.AnyAsync(x => x.Id == share.Id, cancellationToken))
            db.CloudAccessShares.Update(share);
        else
            db.CloudAccessShares.Add(share);
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task DeleteShareAsync(Guid id, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var share = await db.CloudAccessShares.FindAsync([id], cancellationToken);
        if (share is null) return;
        db.CloudAccessShares.Remove(share);
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task<List<CloudAccessGrant>> GetGrantsAsync(Guid shareId, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        return await db.CloudAccessGrants.AsNoTracking()
            .Where(x => x.ShareId == shareId)
            .ToListAsync(cancellationToken);
    }

    public async Task ReplaceGrantsAsync(Guid shareId, IEnumerable<CloudAccessGrant> grants, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var existing = await db.CloudAccessGrants.Where(x => x.ShareId == shareId).ToListAsync(cancellationToken);
        db.CloudAccessGrants.RemoveRange(existing);
        db.CloudAccessGrants.AddRange(grants.GroupBy(x => x.PrincipalId).Select(x => x.First()));
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task<bool> HasGrantAsync(Guid shareId, IEnumerable<Guid> principalIds, CancellationToken cancellationToken = default)
    {
        var ids = principalIds.Distinct().ToArray();
        if (ids.Length == 0) return false;
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        return await db.CloudAccessGrants.AsNoTracking()
            .AnyAsync(x => x.ShareId == shareId && ids.Contains(x.PrincipalId), cancellationToken);
    }

    public async Task<CloudAccessPermission?> GetEffectivePermissionAsync(
        Guid shareId, IEnumerable<Guid> principalIds, CancellationToken cancellationToken = default)
    {
        var ids = principalIds.Distinct().ToArray();
        if (ids.Length == 0) return null;
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var levels = await db.CloudAccessGrants.AsNoTracking()
            .Where(x => x.ShareId == shareId && ids.Contains(x.PrincipalId))
            .Select(x => x.Permission)
            .ToListAsync(cancellationToken);
        // Write (1) outranks Read (0); null when the actor holds no grant at all.
        return levels.Count == 0 ? null : levels.Max();
    }
}

using Kaimo_File_Server.Core.Domain;
using Kaimo_File_Server.Core.Repositories;
using Kaimo_File_Server.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Kaimo_File_Server.Infrastructure.Repositories;

public sealed class CloudAccessRepository(IDbContextFactory<ApplicationDbContext> dbFactory)
    : ICloudAccessRepository
{
    public async Task<List<CloudAccessConnection>> GetConnectionsAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        return await db.CloudAccessConnections.AsNoTracking().OrderBy(x => x.Name).ToListAsync(cancellationToken);
    }

    public async Task<CloudAccessConnection?> GetConnectionAsync(Guid id, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        return await db.CloudAccessConnections.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id, cancellationToken);
    }

    public async Task UpsertConnectionAsync(CloudAccessConnection connection, CancellationToken cancellationToken = default)
    {
        connection.UpdatedAtUtc = DateTime.UtcNow;
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        if (await db.CloudAccessConnections.AnyAsync(x => x.Id == connection.Id, cancellationToken))
            db.CloudAccessConnections.Update(connection);
        else
            db.CloudAccessConnections.Add(connection);
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task UpdateConnectionRuntimeAsync(
        Guid id,
        string protectedCredentials,
        string? accountDisplayName,
        string? accountEmail,
        CloudAccessConnectionState state,
        string? lastError,
        CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var connection = await db.CloudAccessConnections.SingleOrDefaultAsync(x => x.Id == id, cancellationToken)
                         ?? throw new InvalidOperationException("Cloud Access connection no longer exists.");
        connection.ProtectedCredentials = protectedCredentials;
        connection.AccountDisplayName = accountDisplayName ?? connection.AccountDisplayName;
        connection.AccountEmail = accountEmail ?? connection.AccountEmail;
        connection.State = state;
        connection.LastError = lastError;
        connection.LastVerifiedAtUtc = state == CloudAccessConnectionState.Ready ? DateTime.UtcNow : connection.LastVerifiedAtUtc;
        connection.UpdatedAtUtc = DateTime.UtcNow;
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task DeleteConnectionAsync(Guid id, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var connection = await db.CloudAccessConnections.FindAsync([id], cancellationToken);
        if (connection is null) return;
        db.CloudAccessConnections.Remove(connection);
        await db.SaveChangesAsync(cancellationToken);
    }

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

    public async Task<HashSet<Guid>> GetPrincipalIdsAsync(Guid shareId, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        return (await db.CloudAccessGrants.AsNoTracking()
            .Where(x => x.ShareId == shareId)
            .Select(x => x.PrincipalId)
            .ToListAsync(cancellationToken)).ToHashSet();
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
}

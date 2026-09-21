using Kaimo_File_Server.Core.Domain;
using Kaimo_File_Server.Core.Repositories;
using Kaimo_File_Server.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Kaimo_File_Server.Infrastructure.Repositories;

/// <summary>EF Core implementation of <see cref="IShareLinkRepository"/>.</summary>
public sealed class ShareLinkRepository : IShareLinkRepository
{
    private readonly IDbContextFactory<ApplicationDbContext> _dbFactory;

    public ShareLinkRepository(IDbContextFactory<ApplicationDbContext> dbFactory)
        => _dbFactory = dbFactory;

    public async Task<ShareLink> CreateAsync(ShareLink link)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        db.ShareLinks.Add(link);
        await db.SaveChangesAsync();
        return link;
    }

    public async Task UpdateAsync(ShareLink link)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        link.UpdatedAtUtc = DateTime.UtcNow;
        db.ShareLinks.Update(link);
        await db.SaveChangesAsync();
    }

    public async Task DeleteAsync(Guid id)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        var link = await db.ShareLinks.FindAsync(id);
        if (link is null) return;
        db.ShareLinks.Remove(link);
        await db.SaveChangesAsync();
    }

    public async Task<ShareLink?> GetByIdAsync(Guid id)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        return await db.ShareLinks.FindAsync(id);
    }

    public async Task<ShareLink?> GetByTokenAsync(string token)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        return await db.ShareLinks.FirstOrDefaultAsync(l => l.Token == token);
    }

    public async Task<List<ShareLink>> ListForSharesAsync(IEnumerable<Guid> shareIds)
    {
        var ids = shareIds.ToHashSet();
        await using var db = await _dbFactory.CreateDbContextAsync();
        return await db.ShareLinks
            .Where(l => ids.Contains(l.ShareId))
            .OrderByDescending(l => l.CreatedAtUtc)
            .ToListAsync();
    }

    public async Task<List<ShareLink>> ListAllAsync()
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        return await db.ShareLinks
            .OrderByDescending(l => l.CreatedAtUtc)
            .ToListAsync();
    }

    public async Task<ShareLink?> TryConsumeAccessAsync(string token)
    {
        var now = DateTime.UtcNow;
        await using var db = await _dbFactory.CreateDbContextAsync();

        // Single atomic, guarded UPDATE: the database evaluates the window + count
        // predicate and the increment together, so concurrent downloads can never
        // push AccessCount past MaxAccessCount. Portable across PostgreSQL and SQLite.
        int affected = await db.ShareLinks
            .Where(l => l.Token == token
                        && l.IsEnabled
                        && (l.StartsAtUtc == null || l.StartsAtUtc <= now)
                        && (l.ExpiresAtUtc == null || l.ExpiresAtUtc >= now)
                        && (l.MaxAccessCount == null || l.AccessCount < l.MaxAccessCount))
            .ExecuteUpdateAsync(s => s.SetProperty(l => l.AccessCount, l => l.AccessCount + 1));

        return affected == 0 ? null : await GetByTokenAsync(token);
    }
}

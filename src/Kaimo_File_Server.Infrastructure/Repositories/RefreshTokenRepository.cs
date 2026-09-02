using Kaimo_File_Server.Core.Domain.ClientSync;
using Kaimo_File_Server.Core.Repositories;
using Kaimo_File_Server.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Kaimo_File_Server.Infrastructure.Repositories;

/// <summary>EF Core implementation of <see cref="IRefreshTokenRepository"/>.</summary>
public sealed class RefreshTokenRepository : IRefreshTokenRepository
{
    private readonly IDbContextFactory<ApplicationDbContext> _dbFactory;

    public RefreshTokenRepository(IDbContextFactory<ApplicationDbContext> dbFactory)
        => _dbFactory = dbFactory;

    public async Task CreateAsync(RefreshToken token)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        db.RefreshTokens.Add(token);
        await db.SaveChangesAsync();
    }

    public async Task<RefreshToken?> GetByHashAsync(string tokenHash)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        return await db.RefreshTokens.FirstOrDefaultAsync(t => t.TokenHash == tokenHash);
    }

    public async Task<RefreshToken?> GetByIdAsync(Guid id)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        return await db.RefreshTokens.FirstOrDefaultAsync(t => t.Id == id);
    }

    public async Task UpdateAsync(RefreshToken token)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        db.RefreshTokens.Update(token);
        await db.SaveChangesAsync();
    }

    public async Task RotateAsync(RefreshToken current, RefreshToken replacement)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        await using var tx = await db.Database.BeginTransactionAsync();

        db.RefreshTokens.Update(current);
        db.RefreshTokens.Add(replacement);
        await db.SaveChangesAsync();

        await tx.CommitAsync();
    }

    public async Task RevokeAllForDeviceAsync(Guid deviceId, DateTime whenUtc)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        await db.RefreshTokens
            .Where(t => t.DeviceId == deviceId && t.RevokedAtUtc == null)
            .ExecuteUpdateAsync(s => s.SetProperty(t => t.RevokedAtUtc, whenUtc));
    }

    public async Task RevokeAllForUserAsync(Guid userId, DateTime whenUtc)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        await db.RefreshTokens
            .Where(t => t.UserId == userId && t.RevokedAtUtc == null)
            .ExecuteUpdateAsync(s => s.SetProperty(t => t.RevokedAtUtc, whenUtc));
    }

    public async Task<int> PruneExpiredBeforeAsync(DateTime cutoffUtc)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        return await db.RefreshTokens
            .Where(t => t.ExpiresAtUtc < cutoffUtc)
            .ExecuteDeleteAsync();
    }
}

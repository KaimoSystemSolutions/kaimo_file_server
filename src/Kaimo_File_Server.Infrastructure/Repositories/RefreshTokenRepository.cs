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

    public async Task<bool> RotateAsync(RefreshToken current, RefreshToken replacement)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        await using var tx = await db.Database.BeginTransactionAsync();

        // Insert first so the FK-style link target exists, then revoke conditionally:
        // only the rotation that still sees RevokedAtUtc == null may win.
        db.RefreshTokens.Add(replacement);
        await db.SaveChangesAsync();

        int revoked = await db.RefreshTokens
            .Where(t => t.Id == current.Id && t.RevokedAtUtc == null)
            .ExecuteUpdateAsync(s => s
                .SetProperty(t => t.RevokedAtUtc, current.RevokedAtUtc)
                .SetProperty(t => t.ReplacedByTokenId, replacement.Id));

        if (revoked != 1)
        {
            await tx.RollbackAsync();
            return false;
        }

        await tx.CommitAsync();
        return true;
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

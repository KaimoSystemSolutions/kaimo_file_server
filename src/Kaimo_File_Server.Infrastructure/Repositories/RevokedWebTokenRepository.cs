using Kaimo_File_Server.Core.Domain.Identity;
using Kaimo_File_Server.Core.Repositories;
using Kaimo_File_Server.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Kaimo_File_Server.Infrastructure.Repositories;

/// <summary>EF Core implementation of <see cref="IRevokedWebTokenRepository"/>.</summary>
public sealed class RevokedWebTokenRepository(IDbContextFactory<ApplicationDbContext> dbFactory)
    : IRevokedWebTokenRepository
{
    public async Task RevokeAsync(string jti, DateTime expiresAtUtc)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        if (await db.RevokedWebTokens.AnyAsync(t => t.Jti == jti))
            return;

        db.RevokedWebTokens.Add(new RevokedWebToken { Jti = jti, ExpiresAtUtc = expiresAtUtc });
        try
        {
            await db.SaveChangesAsync();
        }
        catch (DbUpdateException)
        {
            // A concurrent logout of the same token inserted it first — already revoked.
            if (!await IsRevokedAsync(jti))
                throw;
        }
    }

    public async Task<bool> IsRevokedAsync(string jti)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        return await db.RevokedWebTokens.AnyAsync(t => t.Jti == jti);
    }

    public async Task<int> PruneExpiredBeforeAsync(DateTime cutoffUtc)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        return await db.RevokedWebTokens
            .Where(t => t.ExpiresAtUtc < cutoffUtc)
            .ExecuteDeleteAsync();
    }
}

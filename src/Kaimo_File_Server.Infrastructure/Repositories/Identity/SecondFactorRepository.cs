using Kaimo_File_Server.Core.Domain.Identity;
using Kaimo_File_Server.Core.Repositories.Identity;
using Kaimo_File_Server.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Kaimo_File_Server.Infrastructure.Repositories.Identity;

/// <summary>EF Core implementation of <see cref="ISecondFactorRepository"/>.</summary>
public sealed class SecondFactorRepository(IDbContextFactory<ApplicationDbContext> dbFactory)
    : ISecondFactorRepository
{
    public async Task<UserSecondFactor?> GetAsync(Guid userId)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        return await db.UserSecondFactors.AsNoTracking().SingleOrDefaultAsync(f => f.UserId == userId);
    }

    public async Task<HashSet<Guid>> GetConfirmedUserIdsAsync()
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        return (await db.UserSecondFactors.Where(f => f.ConfirmedAtUtc != null).Select(f => f.UserId).ToListAsync())
            .ToHashSet();
    }

    public Task<bool> ConfirmAsync(Guid userId, string secretProtected, long verifiedStep,
        IReadOnlyCollection<string> recoveryCodeHashes, DateTime nowUtc)
        => dbFactory.ExecuteResilientAsync(async db =>
        {
            await using var tx = await db.Database.BeginTransactionAsync();
            var existing = await db.UserSecondFactors.SingleOrDefaultAsync(f => f.UserId == userId);
            // A replay after a lost acknowledgement finds its own confirmed row and reports it
            // as not applied; the caller then re-reads the state instead of trusting the result.
            if (existing is { ConfirmedAtUtc: not null })
                return false;

            if (existing is null)
            {
                existing = new UserSecondFactor { UserId = userId, CreatedAtUtc = nowUtc };
                db.UserSecondFactors.Add(existing);
            }
            existing.SecretProtected = secretProtected;
            existing.ConfirmedAtUtc = nowUtc;
            existing.LastUsedStep = verifiedStep;

            await db.UserRecoveryCodes.Where(c => c.UserId == userId).ExecuteDeleteAsync();
            db.UserRecoveryCodes.AddRange(recoveryCodeHashes.Select(h => new UserRecoveryCode { UserId = userId, CodeHash = h }));
            await db.SaveChangesAsync();
            await tx.CommitAsync();
            return true;
        });

    public async Task<bool> TryAdvanceStepAsync(Guid userId, long step)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        return await db.UserSecondFactors
            .Where(f => f.UserId == userId && f.ConfirmedAtUtc != null && f.LastUsedStep < step)
            .ExecuteUpdateAsync(s => s.SetProperty(f => f.LastUsedStep, step)) == 1;
    }

    public async Task<bool> TryUseRecoveryCodeAsync(Guid userId, string codeHash, DateTime nowUtc)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        // One row at most: hashes of one user's codes are distinct (random 80-bit codes).
        return await db.UserRecoveryCodes
            .Where(c => c.UserId == userId && c.CodeHash == codeHash && c.UsedAtUtc == null)
            .ExecuteUpdateAsync(s => s.SetProperty(c => c.UsedAtUtc, nowUtc)) > 0;
    }

    public async Task<int> CountUnusedRecoveryCodesAsync(Guid userId)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        return await db.UserRecoveryCodes.CountAsync(c => c.UserId == userId && c.UsedAtUtc == null);
    }

    public Task ReplaceRecoveryCodesAsync(Guid userId, IReadOnlyCollection<string> codeHashes)
        => dbFactory.ExecuteResilientAsync(async db =>
        {
            await using var tx = await db.Database.BeginTransactionAsync();
            await db.UserRecoveryCodes.Where(c => c.UserId == userId).ExecuteDeleteAsync();
            db.UserRecoveryCodes.AddRange(codeHashes.Select(h => new UserRecoveryCode { UserId = userId, CodeHash = h }));
            await db.SaveChangesAsync();
            await tx.CommitAsync();
        });

    public Task<bool> DeleteAsync(Guid userId, bool rotateSecurityStamp = false)
        => dbFactory.ExecuteResilientAsync(async db =>
        {
            await using var tx = await db.Database.BeginTransactionAsync();
            await db.UserRecoveryCodes.Where(c => c.UserId == userId).ExecuteDeleteAsync();
            int removed = await db.UserSecondFactors.Where(f => f.UserId == userId).ExecuteDeleteAsync();
            if (removed > 0 && rotateSecurityStamp)
            {
                var securityStamp = User.NewSecurityStamp();
                await db.Users.Where(u => u.Id == userId)
                    .ExecuteUpdateAsync(u => u.SetProperty(x => x.SecurityStamp, securityStamp));
            }
            await tx.CommitAsync();
            return removed > 0;
        });
}

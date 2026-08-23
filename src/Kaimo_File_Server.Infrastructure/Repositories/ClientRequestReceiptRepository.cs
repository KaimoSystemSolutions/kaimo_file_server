using Kaimo_File_Server.Core.Domain.ClientSync;
using Kaimo_File_Server.Core.Repositories;
using Kaimo_File_Server.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Kaimo_File_Server.Infrastructure.Repositories;

/// <summary>EF Core implementation of <see cref="IClientRequestReceiptRepository"/>.</summary>
public sealed class ClientRequestReceiptRepository : IClientRequestReceiptRepository
{
    private readonly IDbContextFactory<ApplicationDbContext> _dbFactory;

    public ClientRequestReceiptRepository(IDbContextFactory<ApplicationDbContext> dbFactory)
        => _dbFactory = dbFactory;

    public async Task<ClientRequestReceipt?> GetAsync(Guid deviceId, string idempotencyKey)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        return await db.ClientRequestReceipts
            .AsNoTracking()
            .FirstOrDefaultAsync(r => r.DeviceId == deviceId && r.IdempotencyKey == idempotencyKey);
    }

    public async Task<bool> TryInsertAsync(ClientRequestReceipt receipt)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        db.ClientRequestReceipts.Add(receipt);
        try
        {
            await db.SaveChangesAsync();
            return true;
        }
        catch (DbUpdateException)
        {
            // Another concurrent request won the race on the unique
            // (DeviceId, IdempotencyKey) index — the caller replays that receipt.
            return false;
        }
    }

    public async Task<int> PruneOlderThanAsync(DateTime cutoffUtc)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        return await db.ClientRequestReceipts
            .Where(r => r.CreatedAtUtc < cutoffUtc)
            .ExecuteDeleteAsync();
    }
}

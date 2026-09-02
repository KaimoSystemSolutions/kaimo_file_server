using Kaimo_File_Server.Core.Domain.ClientSync;
using Kaimo_File_Server.Core.Repositories;
using Kaimo_File_Server.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Kaimo_File_Server.Infrastructure.Repositories;

/// <summary>EF Core implementation of <see cref="ISyncDeviceRepository"/>.</summary>
public sealed class SyncDeviceRepository : ISyncDeviceRepository
{
    private readonly IDbContextFactory<ApplicationDbContext> _dbFactory;

    public SyncDeviceRepository(IDbContextFactory<ApplicationDbContext> dbFactory)
        => _dbFactory = dbFactory;

    public async Task<SyncDevice?> GetByIdAsync(Guid deviceId)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        return await db.SyncDevices.FindAsync(deviceId);
    }

    public async Task<List<SyncDevice>> GetByUserAsync(Guid userId)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        return await db.SyncDevices
            .Where(d => d.UserId == userId)
            .OrderByDescending(d => d.CreatedAtUtc)
            .ToListAsync();
    }

    public async Task<List<SyncDevice>> GetAllAsync()
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        return await db.SyncDevices
            .OrderByDescending(d => d.LastSeenUtc)
            .ToListAsync();
    }

    public async Task<SyncDevice> CreateAsync(SyncDevice device)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        db.SyncDevices.Add(device);
        await db.SaveChangesAsync();
        return device;
    }

    public async Task UpdateAsync(SyncDevice device)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        db.SyncDevices.Update(device);
        await db.SaveChangesAsync();
    }

    public async Task TouchLastSeenAsync(Guid deviceId, DateTime whenUtc)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        await db.SyncDevices
            .Where(d => d.Id == deviceId)
            .ExecuteUpdateAsync(s => s.SetProperty(d => d.LastSeenUtc, whenUtc));
    }

    public async Task<bool> DeleteAsync(Guid deviceId)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        // Set-based delete; child rows cascade via their ON DELETE CASCADE foreign keys.
        var deleted = await db.SyncDevices
            .Where(d => d.Id == deviceId)
            .ExecuteDeleteAsync();
        return deleted > 0;
    }

    public async Task<int> DeleteRetiredAsync(DateTime revokedBeforeUtc, DateTime inactiveBeforeUtc)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        // A single set-based delete; the child rows (refresh_tokens, device_sync_profiles,
        // client_request_receipts) are removed by their ON DELETE CASCADE foreign keys.
        return await db.SyncDevices
            .Where(d => (d.RevokedAtUtc != null && d.RevokedAtUtc < revokedBeforeUtc)
                        || d.LastSeenUtc < inactiveBeforeUtc)
            .ExecuteDeleteAsync();
    }
}

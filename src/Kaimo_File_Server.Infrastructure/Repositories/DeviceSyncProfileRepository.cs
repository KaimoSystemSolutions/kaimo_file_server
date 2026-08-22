using Kaimo_File_Server.Core.Domain.ClientSync;
using Kaimo_File_Server.Core.Repositories;
using Kaimo_File_Server.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Kaimo_File_Server.Infrastructure.Repositories;

/// <summary>EF Core implementation of <see cref="IDeviceSyncProfileRepository"/>.</summary>
public sealed class DeviceSyncProfileRepository : IDeviceSyncProfileRepository
{
    private readonly IDbContextFactory<ApplicationDbContext> _dbFactory;

    public DeviceSyncProfileRepository(IDbContextFactory<ApplicationDbContext> dbFactory)
        => _dbFactory = dbFactory;

    public async Task<DeviceSyncProfile?> GetByIdAsync(Guid id)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        return await db.DeviceSyncProfiles.FindAsync(id);
    }

    public async Task<List<DeviceSyncProfile>> GetByDeviceAsync(Guid deviceId)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        return await db.DeviceSyncProfiles
            .Where(p => p.DeviceId == deviceId)
            .OrderBy(p => p.CreatedAtUtc)
            .ToListAsync();
    }

    public async Task<List<DeviceSyncProfile>> GetByUserAsync(Guid userId)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        return await db.DeviceSyncProfiles
            .Where(p => p.UserId == userId)
            .OrderBy(p => p.CreatedAtUtc)
            .ToListAsync();
    }

    public async Task<DeviceSyncProfile> CreateAsync(DeviceSyncProfile profile)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        db.DeviceSyncProfiles.Add(profile);
        await db.SaveChangesAsync();
        return profile;
    }

    public async Task UpdateAsync(DeviceSyncProfile profile)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        db.DeviceSyncProfiles.Update(profile);
        await db.SaveChangesAsync();
    }

    public async Task DeleteAsync(Guid id)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        var profile = await db.DeviceSyncProfiles.FindAsync(id);
        if (profile is not null)
        {
            db.DeviceSyncProfiles.Remove(profile);
            await db.SaveChangesAsync();
        }
    }
}

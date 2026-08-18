using Kaimo_File_Server.Core.Domain;
using Kaimo_File_Server.Core.Repositories;
using Kaimo_File_Server.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Kaimo_File_Server.Infrastructure.Repositories;

/// <summary>EF Core persistence for provider-neutral external-storage connections.</summary>
public sealed class StorageConnectionRepository(IDbContextFactory<ApplicationDbContext> dbFactory)
    : IStorageConnectionRepository
{
    public async Task<List<StorageConnection>> GetAllAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        return await db.StorageConnections.AsNoTracking()
            .OrderBy(connection => connection.Name)
            .ToListAsync(cancellationToken);
    }

    public async Task<StorageConnection?> GetAsync(Guid id, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        return await db.StorageConnections.AsNoTracking()
            .SingleOrDefaultAsync(connection => connection.Id == id, cancellationToken);
    }

    public async Task SaveAsync(StorageConnection connection, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(connection);
        var now = DateTime.UtcNow;
        connection.UpdatedAtUtc = now;

        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        if (connection.ConcurrencyVersion == 0)
        {
            connection.ConcurrencyVersion = 1;
            db.StorageConnections.Add(connection);
        }
        else
        {
            var expectedVersion = connection.ConcurrencyVersion;
            connection.ConcurrencyVersion = checked(expectedVersion + 1);
            db.StorageConnections.Update(connection);
            db.Entry(connection).Property(item => item.ConcurrencyVersion).OriginalValue = expectedVersion;
        }

        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException exception)
        {
            connection.ConcurrencyVersion = Math.Max(0, connection.ConcurrencyVersion - 1);
            throw new StorageConnectionConcurrencyException(connection.Id, exception);
        }
    }

    public async Task UpdateRuntimeAsync(
        Guid id,
        string encryptedCredentialPayload,
        string? accountDisplayName,
        string? accountEmail,
        StorageConnectionState state,
        string? lastErrorCode,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(encryptedCredentialPayload);
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var connection = await db.StorageConnections.SingleOrDefaultAsync(
                             item => item.Id == id, cancellationToken)
                         ?? throw new InvalidOperationException("Storage connection no longer exists.");
        var now = DateTime.UtcNow;
        connection.EncryptedCredentialPayload = encryptedCredentialPayload;
        connection.CredentialFormatVersion = StorageConnection.CredentialContextVersion;
        connection.ProtectorPurposeVersion = encryptedCredentialPayload.StartsWith("dp:v2:", StringComparison.Ordinal)
            ? StorageConnection.CurrentProtectorPurposeVersion
            : 1;
        connection.CredentialUpdatedAtUtc = now;
        connection.AccountDisplayName = accountDisplayName ?? connection.AccountDisplayName;
        connection.AccountEmail = accountEmail ?? connection.AccountEmail;
        connection.State = state;
        connection.LastErrorCode = lastErrorCode;
        connection.LastVerifiedAtUtc = state == StorageConnectionState.Ready
            ? now
            : connection.LastVerifiedAtUtc;
        connection.UpdatedAtUtc = now;
        connection.ConcurrencyVersion = checked(connection.ConcurrencyVersion + 1);
        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException exception)
        {
            throw new StorageConnectionConcurrencyException(id, exception);
        }
    }

    public async Task<StorageConnectionUsage> GetUsageAsync(
        Guid id,
        CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var virtualShareCount = await db.CloudAccessShares.AsNoTracking()
            .CountAsync(share => share.ConnectionId == id, cancellationToken);

        // SyncDefinition is introduced in package 5. Keeping the explicit field
        // in this contract avoids another API change when that table is added.
        return new StorageConnectionUsage(SyncCount: 0, VirtualShareCount: virtualShareCount);
    }

    public async Task<StorageConnectionDeleteResult> DeleteAsync(
        Guid id,
        CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var connection = await db.StorageConnections.FindAsync([id], cancellationToken);
        if (connection is null)
            return StorageConnectionDeleteResult.NotFound;

        if (await db.CloudAccessShares.AsNoTracking()
                .AnyAsync(share => share.ConnectionId == id, cancellationToken))
            return StorageConnectionDeleteResult.InUse;

        db.StorageConnections.Remove(connection);
        try
        {
            await db.SaveChangesAsync(cancellationToken);
            return StorageConnectionDeleteResult.Deleted;
        }
        catch (DbUpdateException)
        {
            // The restrictive database foreign key is authoritative and closes
            // the race between the usage check and deletion.
            return StorageConnectionDeleteResult.InUse;
        }
    }
}

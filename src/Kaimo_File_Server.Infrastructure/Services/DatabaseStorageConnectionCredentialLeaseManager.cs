using Kaimo_File_Server.Core.Domain;
using Kaimo_File_Server.Core.Services;
using Kaimo_File_Server.Infrastructure.Persistence;
using System.Data.Common;
using Microsoft.EntityFrameworkCore;

namespace Kaimo_File_Server.Infrastructure.Services;

/// <summary>
/// Database-backed renewable leases for connection credential mutation. An
/// expired row can be reclaimed after a process crash; an owner-checked update
/// and delete prevent a former owner from modifying a replacement lease.
/// </summary>
public sealed class DatabaseStorageConnectionCredentialLeaseManager(
    IDbContextFactory<ApplicationDbContext> dbFactory,
    TimeProvider timeProvider) : IStorageConnectionCredentialLeaseManager
{
    private static readonly TimeSpan LeaseLifetime = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan RenewalInterval = TimeSpan.FromSeconds(30);

    /// <inheritdoc />
    public async Task<IStorageConnectionCredentialLease?> TryAcquireAsync(
        Guid connectionId,
        CancellationToken cancellationToken = default)
    {
        if (connectionId == Guid.Empty)
            throw new ArgumentException("A connection ID is required.", nameof(connectionId));

        var leaseId = Guid.NewGuid();
        var now = timeProvider.GetUtcNow().UtcDateTime;
        var expiresAt = now.Add(LeaseLifetime);
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);

        var reclaimed = await db.StorageConnectionCredentialLeases
            .Where(lease => lease.ConnectionId == connectionId && lease.ExpiresAtUtc <= now)
            .ExecuteUpdateAsync(
                update => update
                    .SetProperty(lease => lease.LeaseId, leaseId)
                    .SetProperty(lease => lease.ExpiresAtUtc, expiresAt),
                cancellationToken);
        if (reclaimed == 0)
        {
            db.StorageConnectionCredentialLeases.Add(new StorageConnectionCredentialLease
            {
                ConnectionId = connectionId,
                LeaseId = leaseId,
                ExpiresAtUtc = expiresAt
            });
            try
            {
                await db.SaveChangesAsync(cancellationToken);
            }
            catch (DbUpdateException)
            {
                // A concurrent insert or active owner won. A fresh-context check
                // avoids leaking the failed tracked entity into later work.
                return null;
            }
        }

        return new DatabaseLease(this, connectionId, leaseId);
    }

    /// <returns>False when the lease no longer belongs to this owner.</returns>
    private async Task<bool> RenewAsync(Guid connectionId, Guid leaseId, CancellationToken cancellationToken)
    {
        var expiresAt = timeProvider.GetUtcNow().UtcDateTime.Add(LeaseLifetime);
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        return 0 < await db.StorageConnectionCredentialLeases
            .Where(lease => lease.ConnectionId == connectionId && lease.LeaseId == leaseId)
            .ExecuteUpdateAsync(
                update => update.SetProperty(lease => lease.ExpiresAtUtc, expiresAt),
                cancellationToken);
    }

    private async Task ReleaseAsync(Guid connectionId, Guid leaseId)
    {
        try
        {
            await using var db = await dbFactory.CreateDbContextAsync();
            _ = await db.StorageConnectionCredentialLeases
                .Where(lease => lease.ConnectionId == connectionId && lease.LeaseId == leaseId)
                .ExecuteDeleteAsync();
        }
        catch (Exception exception) when (exception is DbException or InvalidOperationException or TimeoutException)
        {
            // Releasing is an optimization: the row expires after LeaseLifetime and
            // is then reclaimed. A failed delete must not fail the completed work.
        }
    }

    private sealed class DatabaseLease : IStorageConnectionCredentialLease
    {
        private readonly DatabaseStorageConnectionCredentialLeaseManager _owner;
        private readonly Guid _connectionId;
        private readonly Guid _leaseId;
        private readonly CancellationTokenSource _renewalCancellation = new();
        private readonly Task _renewalTask;
        private int _disposed;

        public DatabaseLease(
            DatabaseStorageConnectionCredentialLeaseManager owner,
            Guid connectionId,
            Guid leaseId)
        {
            _owner = owner;
            _connectionId = connectionId;
            _leaseId = leaseId;
            _renewalTask = RenewLoopAsync();
        }

        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            try
            {
                await _renewalCancellation.CancelAsync();
                await _renewalTask;
            }
            catch (OperationCanceledException)
            {
                // Normal completion stops the renewal heartbeat.
            }
            finally
            {
                _renewalCancellation.Dispose();
                await _owner.ReleaseAsync(_connectionId, _leaseId);
            }
        }

        private async Task RenewLoopAsync()
        {
            using var timer = new PeriodicTimer(RenewalInterval);
            while (await timer.WaitForNextTickAsync(_renewalCancellation.Token))
            {
                try
                {
                    // Lost (expired and reclaimed by another owner): nothing left to renew.
                    if (!await _owner.RenewAsync(_connectionId, _leaseId, _renewalCancellation.Token))
                        return;
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    // A transient database error must not end the heartbeat; the
                    // next tick retries well before the lease lifetime runs out.
                }
            }
        }
    }
}

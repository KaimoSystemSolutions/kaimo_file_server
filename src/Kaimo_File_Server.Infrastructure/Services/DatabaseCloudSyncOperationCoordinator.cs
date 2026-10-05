using System.Data;
using System.Text.Json;
using Kaimo_File_Server.Core.Helpers;
using Kaimo_File_Server.Core.Services;
using Kaimo_File_Server.Infrastructure.Configuration;
using Kaimo_File_Server.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace Kaimo_File_Server.Infrastructure.Services;

/// <summary>
/// Cross-process cloud-sync coordinator backed by the existing configuration
/// table. Web, scheduled workers, and the SMB bridge therefore observe the same
/// per-share lease even when they run in separate containers.
/// </summary>
public sealed class DatabaseCloudSyncOperationCoordinator(
    IDbContextFactory<ApplicationDbContext> dbFactory,
    TimeProvider timeProvider,
    ILogger<DatabaseCloudSyncOperationCoordinator>? logger = null) : ICloudSyncOperationCoordinator
{
    private const string KeyPrefix = "runtime.cloud-sync-operation.";
    private static readonly TimeSpan RenewableLeaseLifetime =
        TimeSpan.FromMinutes(5);
    private static readonly TimeSpan RenewalInterval =
        TimeSpan.FromMinutes(1);
    // Absolute ceiling on a renewable lease regardless of its heartbeat. If a sync
    // is abandoned while its renewal loop keeps running (for example a disconnected
    // circuit that never disposes the lease), the lease still expires after this so
    // a wedged share recovers on its own instead of staying "busy or unavailable"
    // until a full server restart.
    // ponytail: 6h absolute cap; make it a config setting only if a real sync legitimately runs longer.
    private static readonly TimeSpan MaxLeaseLifetime =
        TimeSpan.FromHours(6);

    public async Task<ICloudSyncOperationLease?> TryBeginSyncAsync(
        Guid shareId,
        string localPath,
        CancellationToken cancellationToken = default)
        => await TryBeginRenewableAsync(
            shareId,
            LeaseKind.Sync,
            ShareRelativePath.Normalize(localPath),
            null,
            cancellationToken);

    public async Task<ICloudSyncOperationLease?> TryBeginPathMutationAsync(
        Guid shareId,
        string oldPath,
        string newPath,
        CancellationToken cancellationToken = default)
        => await TryBeginRenewableAsync(
            shareId,
            LeaseKind.PathMutation,
            ShareRelativePath.Normalize(oldPath),
            ShareRelativePath.Normalize(newPath),
            cancellationToken);

    public async Task<ICloudSyncOperationLease?> TryBeginShareMutationAsync(
        Guid shareId,
        CancellationToken cancellationToken = default)
        => await TryBeginRenewableAsync(
            shareId,
            LeaseKind.ShareMutation,
            "",
            null,
            cancellationToken);

    public async Task<bool> TryReserveExternalPathMutationAsync(
        Guid shareId,
        string oldPath,
        string newPath,
        TimeSpan lifetime,
        CancellationToken cancellationToken = default)
    {
        if (lifetime <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(lifetime));

        Guid leaseId = Guid.NewGuid();
        DateTimeOffset now = timeProvider.GetUtcNow();
        return await TryAcquireAsync(
            shareId,
            new LeasePayload(
                leaseId,
                LeaseKind.ExternalPathMutation,
                ShareRelativePath.Normalize(oldPath),
                ShareRelativePath.Normalize(newPath),
                now.Add(lifetime),
                now),
            cancellationToken);
    }

    public async Task CompleteExternalPathMutationAsync(
        Guid shareId,
        string oldPath,
        string newPath,
        CancellationToken cancellationToken = default)
    {
        string normalizedOld = ShareRelativePath.Normalize(oldPath);
        string normalizedNew = ShareRelativePath.Normalize(newPath);
        string key = GetKey(shareId);
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var setting = await db.ConfigSettings.AsNoTracking()
            .SingleOrDefaultAsync(entry => entry.Key == key, cancellationToken);
        if (setting is null || !TryParse(setting.Value, out var payload) ||
            payload.Kind != LeaseKind.ExternalPathMutation ||
            !string.Equals(
                payload.OldPath, normalizedOld,
                StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(
                payload.NewPath, normalizedNew,
                StringComparison.OrdinalIgnoreCase))
            return;

        await DeleteIfUnchangedAsync(db, key, setting.Value, cancellationToken);
    }

    private async Task<ICloudSyncOperationLease?> TryBeginRenewableAsync(
        Guid shareId,
        LeaseKind kind,
        string oldPath,
        string? newPath,
        CancellationToken cancellationToken)
    {
        Guid leaseId = Guid.NewGuid();
        DateTimeOffset now = timeProvider.GetUtcNow();
        bool acquired = await TryAcquireAsync(
            shareId,
            new LeasePayload(
                leaseId,
                kind,
                oldPath,
                newPath,
                now.Add(RenewableLeaseLifetime),
                now),
            cancellationToken);
        return acquired
            ? new DatabaseLease(this, shareId, leaseId, now)
            : null;
    }

    private async Task<bool> TryAcquireAsync(
        Guid shareId,
        LeasePayload requested,
        CancellationToken cancellationToken)
    {
        string key = GetKey(shareId);
        for (int attempt = 0; attempt < 3; attempt++)
        {
            bool? acquired = await dbFactory.ExecuteResilientAsync(async db =>
            {
                // A race is answered here instead of by the execution strategy: its replay
                // backoff (seconds per attempt) would stall a contended acquisition, while a
                // short local retry or "busy" is the right answer. Connection failures still
                // propagate to the strategy.
                try
                {
                    return (bool?)await TryAcquireOnceAsync(db, key, requested, cancellationToken);
                }
                catch (Exception exception) when (IsAcquisitionRace(exception))
                {
                    return null;
                }
            }, cancellationToken);
            if (acquired is { } result)
                return result;
            if (attempt < 2)
                await Task.Delay(TimeSpan.FromMilliseconds(20), cancellationToken);
        }

        // Still contended after every attempt: another process holds the share.
        return false;
    }

    private async Task<bool> TryAcquireOnceAsync(
        ApplicationDbContext db,
        string key,
        LeasePayload requested,
        CancellationToken cancellationToken)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(
            IsolationLevel.Serializable, cancellationToken);
        var setting = await db.ConfigSettings.SingleOrDefaultAsync(
            entry => entry.Key == key, cancellationToken);
        DateTimeOffset now = timeProvider.GetUtcNow();
        LeasePayload? existing =
            setting is not null && TryParse(setting.Value, out var parsed)
                ? parsed
                : null;
        // A lease past its absolute lifetime is treated as expired even if a
        // leaked renewal loop keeps pushing ExpiresAt forward — this is what
        // lets a wedged share recover on its own without a restart. A payload
        // written before CreatedAt existed (default) keeps the old behavior.
        bool leaseAlive = existing is not null &&
            existing.ExpiresAt > now &&
            !(existing.CreatedAt != default &&
              now >= existing.CreatedAt.Add(MaxLeaseLifetime));
        // A replayed attempt whose first COMMIT went through but whose acknowledgement was
        // lost finds its own lease: that is success, not a busy share (otherwise the lease
        // would linger unrenewed and block the share until it expires).
        if (existing is not null && existing.LeaseId == requested.LeaseId)
        {
            logger?.LogWarning(
                "Cloud-sync lease {LeaseId} for {Key} was already acquired by an attempt whose commit " +
                "acknowledgement was lost; the replay keeps it.", requested.LeaseId, key);
            await transaction.CommitAsync(cancellationToken);
            return true;
        }

        if (leaseAlive)
        {
            bool renewsSameExternalReservation =
                existing!.Kind == LeaseKind.ExternalPathMutation &&
                requested.Kind == LeaseKind.ExternalPathMutation &&
                string.Equals(
                    existing.OldPath, requested.OldPath,
                    StringComparison.OrdinalIgnoreCase) &&
                string.Equals(
                    existing.NewPath, requested.NewPath,
                    StringComparison.OrdinalIgnoreCase);
            if (!renewsSameExternalReservation)
            {
                await transaction.RollbackAsync(cancellationToken);
                return false;
            }
        }

        if (setting is null)
        {
            db.ConfigSettings.Add(new ConfigSetting
            {
                Key = key,
                Value = JsonSerializer.Serialize(requested),
                UpdatedAt = now.UtcDateTime
            });
        }
        else
        {
            setting.Value = JsonSerializer.Serialize(requested);
            setting.UpdatedAt = now.UtcDateTime;
        }

        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return true;
    }

    /// <summary>
    /// Extends the lease. Returns false once this process no longer holds it (expired and
    /// taken over by another process, or removed). Internal for coordinator tests.
    /// </summary>
    internal async Task<bool> RenewAsync(
        Guid shareId,
        Guid leaseId,
        CancellationToken cancellationToken)
    {
        string key = GetKey(shareId);
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var setting = await db.ConfigSettings.AsNoTracking()
            .SingleOrDefaultAsync(entry => entry.Key == key, cancellationToken);
        if (setting is null || !TryParse(setting.Value, out var payload) ||
            payload.LeaseId != leaseId)
            return false;

        DateTimeOffset now = timeProvider.GetUtcNow();
        // Never renew past the absolute cap, so a leaked heartbeat cannot keep a
        // lease alive forever; once the cap passes the lease is left to expire.
        DateTimeOffset renewedExpiry = now.Add(RenewableLeaseLifetime);
        if (payload.CreatedAt != default)
        {
            DateTimeOffset cap = payload.CreatedAt.Add(MaxLeaseLifetime);
            if (renewedExpiry > cap)
                renewedExpiry = cap;
        }

        string renewed = JsonSerializer.Serialize(payload with
        {
            ExpiresAt = renewedExpiry
        });
        DateTime updatedAt = now.UtcDateTime;
        // Compare-and-swap on the value read above: if another process reclaimed
        // the expired lease in between, its row is left untouched.
        if (await db.ConfigSettings
                .Where(entry => entry.Key == key && entry.Value == setting.Value)
                .ExecuteUpdateAsync(update => update
                    .SetProperty(entry => entry.Value, renewed)
                    .SetProperty(entry => entry.UpdatedAt, updatedAt),
                    cancellationToken) == 1)
            return true;

        // A replayed statement (lost acknowledgement) finds the row already holding exactly
        // the value it wrote and matches nothing. That is this renewal, not a lost lease;
        // reporting it as lost would stop the heartbeat while the operation keeps running.
        if (await db.ConfigSettings.AnyAsync(
                entry => entry.Key == key && entry.Value == renewed, cancellationToken))
        {
            logger?.LogWarning(
                "Renewal of cloud-sync lease {LeaseId} for {Key} was already applied by a statement whose " +
                "acknowledgement was lost; the lease is kept.", leaseId, key);
            return true;
        }

        return false;
    }

    private async Task ReleaseAsync(Guid shareId, Guid leaseId)
    {
        string key = GetKey(shareId);
        await using var db = await dbFactory.CreateDbContextAsync();
        var setting = await db.ConfigSettings.AsNoTracking()
            .SingleOrDefaultAsync(entry => entry.Key == key);
        if (setting is null || !TryParse(setting.Value, out var payload) ||
            payload.LeaseId != leaseId)
            return;

        await DeleteIfUnchangedAsync(db, key, setting.Value, CancellationToken.None);
    }

    /// <summary>
    /// Deletes the lease row only if it still holds exactly the value this process
    /// read. A lease that expired and was reclaimed by another process in between
    /// has a different value and therefore survives.
    /// </summary>
    private static Task<int> DeleteIfUnchangedAsync(
        ApplicationDbContext db,
        string key,
        string expectedValue,
        CancellationToken cancellationToken)
        => db.ConfigSettings
            .Where(entry => entry.Key == key && entry.Value == expectedValue)
            .ExecuteDeleteAsync(cancellationToken);

    // A competing acquisition, not a broken connection: serialization failure or duplicate
    // key (SaveChanges wraps them in DbUpdateException). Other database errors, transient
    // connection failures above all, are left to the execution strategy.
    private static bool IsAcquisitionRace(Exception exception)
        => (exception is DbUpdateException ? exception.InnerException : exception) switch
        {
            PostgresException { SqlState: PostgresErrorCodes.SerializationFailure or PostgresErrorCodes.UniqueViolation } => true,
            NpgsqlException => false,
            _ => exception is DbUpdateException
        };

    private static bool TryParse(string json, out LeasePayload payload)
    {
        try
        {
            payload = JsonSerializer.Deserialize<LeasePayload>(json)!;
            return payload is not null;
        }
        catch (JsonException)
        {
            payload = null!;
            return false;
        }
    }

    private void LogLeaseLost(Guid shareId, Guid leaseId)
        => logger?.LogWarning(
            "Cloud-sync lease {LeaseId} for share {ShareId} was lost while its operation was still running " +
            "(expired and taken over by another process); the operations may now overlap.",
            leaseId, shareId);

    private void LogLeaseEnding(Guid shareId, Guid leaseId)
        => logger?.LogWarning(
            "Cloud-sync lease {LeaseId} for share {ShareId} reaches its absolute lifetime of {Cap}; the operation " +
            "is stopped before it expires, so nothing else can overlap it. A sync resumes on its next run.",
            leaseId, shareId, MaxLeaseLifetime);

    private void LogRenewalFailed(Guid shareId, Exception exception)
        => logger?.LogWarning(exception, "Renewing the cloud-sync lease for share {ShareId} failed.", shareId);

    /// <summary>
    /// True once a renewal can no longer extend the lease by a full lifetime, i.e. the lease
    /// ends at its absolute cap within <see cref="RenewableLeaseLifetime"/>.
    /// </summary>
    private bool ReachesLifetimeCap(DateTimeOffset createdAt)
        => timeProvider.GetUtcNow() + RenewableLeaseLifetime >= createdAt + MaxLeaseLifetime;

    internal static string GetKey(Guid shareId) => $"{KeyPrefix}{shareId:N}";

    /// <summary>The absolute lifetime cap, exposed for coordinator tests.</summary>
    internal static TimeSpan LeaseLifetimeCap => MaxLeaseLifetime;

    private sealed class DatabaseLease : ICloudSyncOperationLease
    {
        private readonly DatabaseCloudSyncOperationCoordinator _owner;
        private readonly Guid _shareId;
        private readonly Guid _leaseId;
        private readonly DateTimeOffset _createdAt;
        private readonly CancellationTokenSource _renewalCancellation = new();
        // Not disposed: holders may still read the token after DisposeAsync, and a
        // CancellationTokenSource without a timer holds no unmanaged resources.
        private readonly CancellationTokenSource _leaseLost = new();
        private readonly Task _renewal;
        private int _disposed;

        public CancellationToken LeaseLost => _leaseLost.Token;

        public DatabaseLease(
            DatabaseCloudSyncOperationCoordinator owner,
            Guid shareId,
            Guid leaseId,
            DateTimeOffset createdAt)
        {
            _owner = owner;
            _shareId = shareId;
            _leaseId = leaseId;
            _createdAt = createdAt;
            _renewal = RenewLoopAsync();
        }

        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
                return;

            await _renewalCancellation.CancelAsync();
            try
            {
                await _renewal;
            }
            catch (OperationCanceledException)
            {
                // Expected when a completed operation stops its heartbeat.
            }
            finally
            {
                _renewalCancellation.Dispose();
                try
                {
                    await _owner.ReleaseAsync(_shareId, _leaseId);
                }
                catch (Exception)
                {
                    // Best effort: an unreleased lease simply expires after its lifetime.
                }
            }
        }

        private async Task RenewLoopAsync()
        {
            using var timer = new PeriodicTimer(RenewalInterval);
            while (await timer.WaitForNextTickAsync(
                       _renewalCancellation.Token))
            {
                try
                {
                    bool final = _owner.ReachesLifetimeCap(_createdAt);
                    if (!await _owner.RenewAsync(
                            _shareId, _leaseId, _renewalCancellation.Token))
                    {
                        // Lost to another process; renewing further could never succeed.
                        // Tell a cooperative holder to stop rather than overlap the new owner.
                        _owner.LogLeaseLost(_shareId, _leaseId);
                        await SignalEndAsync();
                        return;
                    }
                    if (final)
                    {
                        // The cap is near: stop the holder now, while the lease still runs
                        // for at least four minutes, instead of letting it expire under a
                        // running operation that another process could then take over. This
                        // also ends a leaked heartbeat, so the lease expires at the cap.
                        _owner.LogLeaseEnding(_shareId, _leaseId);
                        await SignalEndAsync();
                        return;
                    }
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    // A transient database error must not end the heartbeat; the
                    // next tick retries well before the lease lifetime runs out.
                    _owner.LogRenewalFailed(_shareId, exception);
                }
            }
        }

        private async Task SignalEndAsync()
        {
            try
            {
                await _leaseLost.CancelAsync();
            }
            catch (Exception exception)
            {
                // A throwing cancellation callback belongs to the holder; the heartbeat
                // ends either way.
                _owner.LogRenewalFailed(_shareId, exception);
            }
        }
    }

    internal enum LeaseKind
    {
        Sync,
        PathMutation,
        ShareMutation,
        ExternalPathMutation
    }

    internal sealed record LeasePayload(
        Guid LeaseId,
        LeaseKind Kind,
        string OldPath,
        string? NewPath,
        DateTimeOffset ExpiresAt,
        DateTimeOffset CreatedAt = default);
}

using System.Data;
using System.Text.Json;
using Kaimo_File_Server.Core.Helpers;
using Kaimo_File_Server.Core.Services;
using Kaimo_File_Server.Infrastructure.Configuration;
using Kaimo_File_Server.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Kaimo_File_Server.Infrastructure.Services;

/// <summary>
/// Cross-process cloud-sync coordinator backed by the existing configuration
/// table. Web, scheduled workers, and the SMB bridge therefore observe the same
/// per-share lease even when they run in separate containers.
/// </summary>
public sealed class DatabaseCloudSyncOperationCoordinator(
    IDbContextFactory<ApplicationDbContext> dbFactory,
    TimeProvider timeProvider) : ICloudSyncOperationCoordinator
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
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var setting = await db.ConfigSettings.FindAsync(
            [GetKey(shareId)], cancellationToken);
        if (setting is null || !TryParse(setting.Value, out var payload) ||
            payload.Kind != LeaseKind.ExternalPathMutation ||
            !string.Equals(
                payload.OldPath, normalizedOld,
                StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(
                payload.NewPath, normalizedNew,
                StringComparison.OrdinalIgnoreCase))
            return;

        db.ConfigSettings.Remove(setting);
        await db.SaveChangesAsync(cancellationToken);
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
            ? new DatabaseLease(this, shareId, leaseId)
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
            await using var db = await dbFactory.CreateDbContextAsync(
                cancellationToken);
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

            try
            {
                await db.SaveChangesAsync(cancellationToken);
                await transaction.CommitAsync(cancellationToken);
                return true;
            }
            catch (Exception exception) when (
                attempt < 2 && IsAcquisitionRace(exception))
            {
                await transaction.RollbackAsync(CancellationToken.None);
                await Task.Delay(TimeSpan.FromMilliseconds(20), cancellationToken);
            }
        }

        return false;
    }

    private async Task RenewAsync(
        Guid shareId,
        Guid leaseId,
        CancellationToken cancellationToken)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var setting = await db.ConfigSettings.FindAsync(
            [GetKey(shareId)], cancellationToken);
        if (setting is null || !TryParse(setting.Value, out var payload) ||
            payload.LeaseId != leaseId)
            return;

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

        setting.Value = JsonSerializer.Serialize(payload with
        {
            ExpiresAt = renewedExpiry
        });
        setting.UpdatedAt = now.UtcDateTime;
        await db.SaveChangesAsync(cancellationToken);
    }

    private async Task ReleaseAsync(Guid shareId, Guid leaseId)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        var setting = await db.ConfigSettings.FindAsync(GetKey(shareId));
        if (setting is null || !TryParse(setting.Value, out var payload) ||
            payload.LeaseId != leaseId)
            return;

        db.ConfigSettings.Remove(setting);
        await db.SaveChangesAsync();
    }

    private static bool IsAcquisitionRace(Exception exception)
        => exception is DbUpdateException ||
           exception is PostgresException postgres &&
           postgres.SqlState is PostgresErrorCodes.SerializationFailure or
               PostgresErrorCodes.UniqueViolation;

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

    internal static string GetKey(Guid shareId) => $"{KeyPrefix}{shareId:N}";

    /// <summary>The absolute lifetime cap, exposed for coordinator tests.</summary>
    internal static TimeSpan LeaseLifetimeCap => MaxLeaseLifetime;

    private sealed class DatabaseLease : ICloudSyncOperationLease
    {
        private readonly DatabaseCloudSyncOperationCoordinator _owner;
        private readonly Guid _shareId;
        private readonly Guid _leaseId;
        private readonly CancellationTokenSource _renewalCancellation = new();
        private readonly Task _renewal;
        private int _disposed;

        public DatabaseLease(
            DatabaseCloudSyncOperationCoordinator owner,
            Guid shareId,
            Guid leaseId)
        {
            _owner = owner;
            _shareId = shareId;
            _leaseId = leaseId;
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
            }
            await _owner.ReleaseAsync(_shareId, _leaseId);
        }

        private async Task RenewLoopAsync()
        {
            using var timer = new PeriodicTimer(RenewalInterval);
            while (await timer.WaitForNextTickAsync(
                       _renewalCancellation.Token))
                await _owner.RenewAsync(
                    _shareId, _leaseId, _renewalCancellation.Token);
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

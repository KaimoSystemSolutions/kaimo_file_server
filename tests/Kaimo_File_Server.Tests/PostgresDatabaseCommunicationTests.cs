using System.Data.Common;
using Kaimo_File_Server.Core.Domain;
using Kaimo_File_Server.Core.Domain.ClientSync;
using Kaimo_File_Server.Core.Domain.Identity;
using Kaimo_File_Server.Core.Domain.Notifications;
using Kaimo_File_Server.Core.Services.Notifications;
using Kaimo_File_Server.Infrastructure.Repositories;
using Kaimo_File_Server.Infrastructure.Services;
using Kaimo_File_Server.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Npgsql;
using Xunit;

namespace Kaimo_File_Server.Tests;

/// <summary>
/// Cross-process database behavior that only PostgreSQL exhibits: the retrying execution
/// strategy, serializable isolation and row locks. Skipped unless <c>KAIMO_TEST_PG</c> is set.
/// </summary>
public sealed class PostgresDatabaseCommunicationTests : PostgresTestBase
{
    [PostgresFact]
    public async Task SambaRename_WithLeaseRenewalDuringTheTransaction_RunsOnceWithoutSerializationReplay()
    {
        var shareId = Guid.NewGuid();
        var eventId = Guid.NewGuid();
        var version = new FileVersion(
            shareId, "old.txt", DateTime.UtcNow, "AA/BB/SOURCE.bin.gz", "SOURCE", 10, null, 1);
        await using (var db = NewContext())
        {
            db.FileVersions.Add(version);
            db.SambaLifecycleEventReceipts.Add(new SambaLifecycleEventReceipt
            {
                EventId = eventId,
                EventType = "rename",
                CreatedAtUtc = DateTime.UtcNow,
                LeaseUntilUtc = DateTime.UtcNow.AddMinutes(2),
                AttemptCount = 1
            });
            await db.SaveChangesAsync();
        }

        // The bridge heartbeat renews the lease while the rename transaction is open.
        var leases = new SambaLifecycleEventRepository(DbFactory);
        var heartbeat = new RenewAfterReceiptRead(() => leases.RenewAsync(eventId, TimeSpan.FromMinutes(2)));

        await new FileVersionRepository(FactoryWith(heartbeat))
            .RenamePathAsync(shareId, "old.txt", "new.txt", eventId);
        Assert.True(await heartbeat.Renewal!);

        // One attempt: the renewal waited for the commit instead of forcing a 40001 replay.
        Assert.Equal(1, heartbeat.ReceiptReads);
        await using var assertionDb = NewContext();
        Assert.Equal("new.txt", (await assertionDb.FileVersions.FindAsync(version.Id))!.FilePath);
        Assert.NotNull((await assertionDb.SambaLifecycleEventReceipts.FindAsync(eventId))!.RenameVersionsCompletedAtUtc);
    }

    // ── Replay after a lost COMMIT acknowledgement ──────────────────────────────
    // The commit reaches the database, but the client sees a transient I/O error, so the
    // execution strategy runs the whole unit a second time against the committed state.

    [PostgresFact]
    public async Task NotificationCompletion_ReplayedAfterLostCommitAck_KeepsTheEventDone()
    {
        var repository = new NotificationRepository(DbFactory);
        await repository.AddEventAsync(
            NotificationEvents.UploadReceived(Guid.NewGuid(), "Bewerbungen", Guid.NewGuid(), "cv.pdf", 1));
        var claimed = Assert.Single(await repository.ClaimEventsAsync(10, DateTime.UtcNow.AddMinutes(2)));
        var delivery = new MailDelivery
        {
            EventSeq = claimed.Seq, EventType = claimed.Type, ToAddress = "a@example.test", Subject = "s"
        };

        await new NotificationRepository(FactoryWith(new LoseFirstCommitAcknowledgement()))
            .CompleteEventWithDeliveriesAsync(claimed.Seq, [delivery], NotificationEventStatus.Done);

        await using var db = NewContext();
        Assert.Equal(NotificationEventStatus.Done, (await db.NotificationEvents.SingleAsync()).Status);
        Assert.Single(await db.MailDeliveries.ToListAsync());
    }

    [PostgresFact]
    public async Task NotificationEvent_InsertReplayedAfterLostCommitAck_IsStoredOnce()
    {
        var notification = NotificationEvents.UploadReceived(Guid.NewGuid(), "Bewerbungen", Guid.NewGuid(), "cv.pdf", 1);

        await new NotificationRepository(FactoryWith(new LoseFirstCommitAcknowledgement()))
            .AddEventAsync(notification);

        // Before the fix the replay inserted a second event under a new Seq: duplicate mails.
        await using var db = NewContext();
        var stored = Assert.Single(await db.NotificationEvents.ToListAsync());
        Assert.Equal(stored.Seq, notification.Seq);
    }

    [PostgresFact]
    public async Task NotificationClaim_StatementReplayedAfterLostAck_KeepsTheClaim()
    {
        await new NotificationRepository(DbFactory).AddEventAsync(
            NotificationEvents.UploadReceived(Guid.NewGuid(), "Bewerbungen", Guid.NewGuid(), "cv.pdf", 1));

        var claimed = await new NotificationRepository(FactoryWith(new LoseFirstUpdateAcknowledgement()))
            .ClaimEventsAsync(10, DateTime.UtcNow.AddMinutes(2));

        // Before the fix the replayed UPDATE matched no row and the event sat unclaimed in
        // Processing until its lease expired.
        var only = Assert.Single(claimed);
        Assert.Equal(NotificationEventStatus.Processing, only.Status);
        Assert.Equal(1, only.AttemptCount);
    }

    [PostgresFact]
    public async Task RefreshRotation_ReplayedAfterLostCommitAck_ReportsSuccess()
    {
        var now = DateTime.UtcNow;
        var user = new User(Guid.NewGuid(), "bob", "bob", passwordHash: "pw-hash", ntHash: "nt-hash", isEnabled: true);
        var device = new SyncDevice
        {
            Id = Guid.NewGuid(), UserId = user.Id, DisplayName = "dev", CreatedAtUtc = now, LastSeenUtc = now
        };
        RefreshToken Token() => new()
        {
            Id = Guid.NewGuid(), UserId = user.Id, DeviceId = device.Id,
            TokenHash = Guid.NewGuid().ToString("N") + Guid.NewGuid().ToString("N"),
            CreatedAtUtc = now, ExpiresAtUtc = now.AddDays(30)
        };
        var current = Token();
        await using (var db = NewContext())
        {
            db.Users.Add(user);
            db.SyncDevices.Add(device);
            db.RefreshTokens.Add(current);
            await db.SaveChangesAsync();
        }

        current.RevokedAtUtc = now;
        var replacement = Token();
        Assert.True(await new RefreshTokenRepository(FactoryWith(new LoseFirstCommitAcknowledgement()))
            .RotateAsync(current, replacement));

        await using var assertionDb = NewContext();
        Assert.Equal(replacement.Id,
            (await assertionDb.RefreshTokens.SingleAsync(t => t.Id == current.Id)).ReplacedByTokenId);
        Assert.Equal(2, await assertionDb.RefreshTokens.CountAsync());
    }

    [PostgresFact]
    public async Task LeaseAcquisition_ReplayedAfterLostCommitAck_ReturnsTheLease()
    {
        var coordinator = new DatabaseCloudSyncOperationCoordinator(
            FactoryWith(new LoseFirstCommitAcknowledgement()), TimeProvider.System);

        var lease = await coordinator.TryBeginSyncAsync(Guid.NewGuid(), "projects");

        // Before the fix the replay saw its own lease as "busy" and the share stayed blocked.
        Assert.NotNull(lease);
        await lease.DisposeAsync();
        await using var db = NewContext();
        Assert.Empty(await db.ConfigSettings.ToListAsync());
    }

    [PostgresFact]
    public async Task LeaseRenewal_StatementReplayedAfterLostAck_KeepsTheLease()
    {
        var shareId = Guid.NewGuid();
        var lease = await new DatabaseCloudSyncOperationCoordinator(DbFactory, TimeProvider.System)
            .TryBeginSyncAsync(shareId, "projects");
        Assert.NotNull(lease);
        Guid leaseId;
        await using (var db = NewContext())
        {
            var stored = await db.ConfigSettings.SingleAsync();
            using var json = System.Text.Json.JsonDocument.Parse(stored.Value);
            leaseId = json.RootElement.GetProperty("LeaseId").GetGuid();
        }

        var renewed = await new DatabaseCloudSyncOperationCoordinator(
                FactoryWith(new LoseFirstUpdateAcknowledgement()), TimeProvider.System)
            .RenewAsync(shareId, leaseId, CancellationToken.None);

        // Before the fix the replayed compare-and-swap matched no row, the lease counted as
        // lost and its heartbeat stopped while the sync was still running.
        Assert.True(renewed);
        await lease.DisposeAsync();
    }

    [PostgresFact]
    public async Task RenameWithoutEvent_ReplayedAfterLostCommitAck_KeepsTheMovedHistory()
    {
        var shareId = Guid.NewGuid();
        var version = new FileVersion(
            shareId, "old.txt", DateTime.UtcNow, "AA/BB/SOURCE.bin.gz", "SOURCE", 10, null, 1);
        var replaced = new FileVersion(
            shareId, "new.txt", DateTime.UtcNow, "CC/DD/TARGET.bin.gz", "TARGET", 10, null, 1);
        await using (var db = NewContext())
        {
            db.FileVersions.AddRange(version, replaced);
            await db.SaveChangesAsync();
        }

        var displaced = await new FileVersionRepository(FactoryWith(new LoseFirstCommitAcknowledgement()))
            .RenamePathAsync(shareId, "old.txt", "new.txt");

        // Before the fix the replay took the moved versions for displaced ones and deleted them.
        // The replaced version is still reported, so its blob is removed.
        Assert.Equal(replaced.Id, Assert.Single(displaced).Id);
        await using var assertionDb = NewContext();
        var remaining = await assertionDb.FileVersions.SingleAsync();
        Assert.Equal(version.Id, remaining.Id);
        Assert.Equal("new.txt", remaining.FilePath);
    }

    [PostgresFact]
    public async Task RenameWithoutEvent_ReplayedAfterRolledBackCommit_RemovesTheDisplacedHistory()
    {
        // No source versions, only versions of the file the rename replaces.
        var shareId = Guid.NewGuid();
        var replaced = new FileVersion(
            shareId, "new.txt", DateTime.UtcNow, "CC/DD/TARGET.bin.gz", "TARGET", 10, null, 1);
        await using (var db = NewContext())
        {
            db.FileVersions.Add(replaced);
            await db.SaveChangesAsync();
        }

        var displaced = await new FileVersionRepository(FactoryWith(new FailFirstCommit()))
            .RenamePathAsync(shareId, "old.txt", "new.txt");

        // Before the fix the replay found an empty source, assumed a committed first attempt
        // and left the replaced file's history attached to the renamed one.
        Assert.Equal(replaced.Id, Assert.Single(displaced).Id);
        await using var assertionDb = NewContext();
        Assert.Empty(await assertionDb.FileVersions.ToListAsync());
    }

    [PostgresFact]
    public async Task SambaLeaseRenewal_ReclaimedWhileWaitingForTheRowLock_DoesNotTouchTheNewClaim()
    {
        var eventId = Guid.NewGuid();
        var lease = DateTime.UtcNow.AddMinutes(2);
        await using (var db = NewContext())
        {
            db.SambaLifecycleEventReceipts.Add(new SambaLifecycleEventReceipt
            {
                EventId = eventId,
                EventType = "rename",
                CreatedAtUtc = DateTime.UtcNow,
                LeaseUntilUtc = lease,
                AttemptCount = 1
            });
            await db.SaveChangesAsync();
        }

        // Another transaction holds the row (as a rename does) and, before it commits, the
        // event is re-claimed with a new lease.
        var reclaimedLease = DateTime.UtcNow.AddMinutes(10);
        await using var blocker = new NpgsqlConnection(ConnectionString);
        await blocker.OpenAsync();
        await using var tx = await blocker.BeginTransactionAsync();
        await using (var reclaim = new NpgsqlCommand(
            "UPDATE samba_lifecycle_event_receipts SET \"LeaseUntilUtc\" = @lease WHERE \"EventId\" = @id",
            blocker, tx))
        {
            reclaim.Parameters.AddWithValue("lease", reclaimedLease);
            reclaim.Parameters.AddWithValue("id", eventId);
            await reclaim.ExecuteNonQueryAsync();
        }

        var renewal = new SambaLifecycleEventRepository(DbFactory).RenewAsync(eventId, TimeSpan.FromMinutes(2));
        await Task.Delay(500);
        Assert.False(renewal.IsCompleted);
        await tx.CommitAsync();

        // Before the fix the renewal re-checked against the clock after the wait and
        // overwrote the new claim's lease, extending another handler's claim.
        Assert.False(await renewal);
        await using var assertionDb = NewContext();
        var stored = (await assertionDb.SambaLifecycleEventReceipts.FindAsync(eventId))!.LeaseUntilUtc!.Value;
        Assert.True(Math.Abs((stored - reclaimedLease).TotalMilliseconds) < 1);
    }

    /// <summary>
    /// Lets the first COMMIT reach the database, then fails it with a transient I/O error,
    /// which is what a connection drop during the commit acknowledgement looks like.
    /// </summary>
    private sealed class LoseFirstCommitAcknowledgement : DbTransactionInterceptor
    {
        private bool _lost;

        public override Task TransactionCommittedAsync(
            DbTransaction transaction, TransactionEndEventData eventData,
            CancellationToken cancellationToken = default)
        {
            if (_lost) return Task.CompletedTask;
            _lost = true;
            throw new NpgsqlException("Commit acknowledgement lost", new IOException("Connection reset"));
        }
    }

    /// <summary>
    /// Fails the first COMMIT with a transient I/O error before it reaches the database, so
    /// the attempt rolls back and the execution strategy replays the unit.
    /// </summary>
    private sealed class FailFirstCommit : DbTransactionInterceptor
    {
        private bool _failed;

        public override ValueTask<InterceptionResult> TransactionCommittingAsync(
            DbTransaction transaction, TransactionEventData eventData, InterceptionResult result,
            CancellationToken cancellationToken = default)
        {
            if (_failed) return ValueTask.FromResult(result);
            _failed = true;
            throw new NpgsqlException("Connection lost before commit", new IOException("Connection reset"));
        }
    }

    /// <summary>
    /// Lets the first auto-committed UPDATE reach the database, then fails it with a transient
    /// I/O error, so the execution strategy runs the statement a second time.
    /// </summary>
    private sealed class LoseFirstUpdateAcknowledgement : DbCommandInterceptor
    {
        private bool _lost;

        public override ValueTask<int> NonQueryExecutedAsync(
            DbCommand command, CommandExecutedEventData eventData, int result,
            CancellationToken cancellationToken = default)
        {
            if (_lost || !command.CommandText.TrimStart().StartsWith("UPDATE", StringComparison.OrdinalIgnoreCase))
                return ValueTask.FromResult(result);
            _lost = true;
            throw new NpgsqlException("Acknowledgement lost", new IOException("Connection reset"));
        }
    }

    /// <summary>
    /// Starts a lease renewal on another connection right after the rename transaction has
    /// read its receipt, and gives it time to commit before the transaction continues.
    /// </summary>
    private sealed class RenewAfterReceiptRead(Func<Task<bool>> renew) : DbCommandInterceptor
    {
        public int ReceiptReads { get; private set; }
        public Task<bool>? Renewal { get; private set; }

        public override async ValueTask<DbDataReader> ReaderExecutedAsync(
            DbCommand command, CommandExecutedEventData eventData, DbDataReader result,
            CancellationToken cancellationToken = default)
        {
            var sql = command.CommandText.TrimStart();
            if (sql.StartsWith("SELECT", StringComparison.OrdinalIgnoreCase)
                && sql.Contains("samba_lifecycle_event_receipts", StringComparison.Ordinal))
            {
                ReceiptReads++;
                if (Renewal is null)
                {
                    Renewal = Task.Run(renew);
                    await Task.Delay(500, cancellationToken);
                }
            }
            return result;
        }
    }
}

using Kaimo_File_Server.Core.Domain;
using Kaimo_File_Server.Core.Domain.Notifications;
using Kaimo_File_Server.Core.Services.Notifications;
using Kaimo_File_Server.Infrastructure;
using Kaimo_File_Server.Infrastructure.Configuration;
using Kaimo_File_Server.Infrastructure.Repositories;
using Kaimo_File_Server.Infrastructure.Services;
using Kaimo_File_Server.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Caching.Memory;
using Xunit;

namespace Kaimo_File_Server.Tests;

/// <summary>
/// Host, Web and SmbBridge communicate only through the shared database. These tests pin
/// the hardening of that channel: config upserts survive a concurrent insert, a lease never
/// lets a database error escape its disposal, and non-owner processes only start against a
/// schema that is fully migrated, seeded and not newer than their own build.
/// </summary>
public sealed class DatabaseCommunicationHardeningTests : DatabaseTestBase
{
    [Fact]
    public async Task ConfigSet_WhenAnotherProcessInsertsTheSameKeyFirst_UpdatesInsteadOfFailing()
    {
        const string key = "runtime.race";
        var repo = new ConfigRepository(
            FactoryWith(new InsertSameKeyBeforeFirstSave(DbFactory, key)),
            new MemoryCache(new MemoryCacheOptions()));

        await repo.SetAsync(key, "mine");

        await using var db = NewContext();
        var stored = await db.ConfigSettings.AsNoTracking().SingleAsync(s => s.Key == key);
        Assert.Equal("mine", stored.Value);
    }

    [Fact]
    public async Task LeaseDispose_DoesNotThrow_WhenTheDatabaseFailsDuringRelease()
    {
        var coordinator = new DatabaseCloudSyncOperationCoordinator(DbFactory, TimeProvider.System);
        var lease = await coordinator.TryBeginSyncAsync(Guid.NewGuid(), "projects");
        Assert.NotNull(lease);

        await using (var db = NewContext())
            await db.Database.ExecuteSqlRawAsync("DROP TABLE config_settings");

        // Before the fix the release error escaped DisposeAsync and broke the caller's
        // cleanup; an unreleased lease simply expires instead.
        await lease.DisposeAsync();
    }

    [Fact]
    public async Task SchemaReadiness_WaitsForSeedMarker_AndRejectsANewerSchema()
    {
        await using var db = NewContext();
        var known = db.Database.GetMigrations().ToList();
        Assert.NotEmpty(known);

        await db.Database.ExecuteSqlRawAsync(
            "CREATE TABLE \"__EFMigrationsHistory\" (\"MigrationId\" TEXT NOT NULL PRIMARY KEY, \"ProductVersion\" TEXT NOT NULL)");
        foreach (var migration in known)
            await db.Database.ExecuteSqlAsync(
                $"INSERT INTO \"__EFMigrationsHistory\" VALUES ({migration}, '10.0.0')");

        // Migrated but not seeded: keep waiting.
        Assert.NotNull(await ServiceCollectionExtensions.CheckSchemaReadyAsync(db));

        db.ConfigSettings.Add(new ConfigSetting
        {
            Key = ServiceCollectionExtensions.SchemaReadyKey,
            Value = known[^1],
            UpdatedAt = DateTime.UtcNow
        });
        await db.SaveChangesAsync();
        Assert.Null(await ServiceCollectionExtensions.CheckSchemaReadyAsync(db));

        // The Host has applied a migration this build does not know: fail at once.
        await db.Database.ExecuteSqlRawAsync(
            "INSERT INTO \"__EFMigrationsHistory\" VALUES ('99991231000000_FromANewerRelease', '10.0.0')");
        await Assert.ThrowsAsync<SchemaVersionMismatchException>(
            () => ServiceCollectionExtensions.CheckSchemaReadyAsync(db));
    }

    [Fact]
    public async Task NotificationCompletion_OnAnAlreadyCompletedEvent_DoesNotRequeueIt()
    {
        var repository = new NotificationRepository(DbFactory);
        await repository.AddEventAsync(
            NotificationEvents.UploadReceived(Guid.NewGuid(), "Bewerbungen", Guid.NewGuid(), "cv.pdf", 1));
        var claimed = Assert.Single(await repository.ClaimEventsAsync(10, DateTime.UtcNow.AddMinutes(2)));
        await repository.CompleteEventWithDeliveriesAsync(claimed.Seq, [], NotificationEventStatus.Skipped);

        // A late or replayed completion (e.g. the failure path after a lost commit
        // acknowledgement) must not send a finished event back to Pending.
        await repository.CompleteEventAsync(claimed.Seq, NotificationEventStatus.Pending, "late");

        await using var db = NewContext();
        Assert.Equal(NotificationEventStatus.Skipped, (await db.NotificationEvents.SingleAsync()).Status);
    }

    [Fact]
    public async Task NotificationCompletion_ByAClaimWhoseLeaseExpired_DoesNotCompleteTheNewClaim()
    {
        var repository = new NotificationRepository(DbFactory);
        await repository.AddEventAsync(
            NotificationEvents.UploadReceived(Guid.NewGuid(), "Bewerbungen", Guid.NewGuid(), "cv.pdf", 1));
        // The first claim's lease is already over, so the event is claimed a second time.
        var stale = Assert.Single(await repository.ClaimEventsAsync(10, DateTime.UtcNow.AddSeconds(-1)));
        var current = Assert.Single(await repository.ClaimEventsAsync(10, DateTime.UtcNow.AddMinutes(2)));

        // The former holder finishes late: it must not complete (and store deliveries for)
        // the event the current holder is processing.
        await repository.CompleteEventWithDeliveriesAsync(
            stale.Seq, [], NotificationEventStatus.Skipped, claimedLeaseUntilUtc: stale.LeaseUntilUtc);
        await using (var db = NewContext())
            Assert.Equal(NotificationEventStatus.Processing, (await db.NotificationEvents.SingleAsync()).Status);

        await repository.CompleteEventWithDeliveriesAsync(
            current.Seq, [], NotificationEventStatus.Skipped, claimedLeaseUntilUtc: current.LeaseUntilUtc);
        await using (var db = NewContext())
            Assert.Equal(NotificationEventStatus.Skipped, (await db.NotificationEvents.SingleAsync()).Status);
    }

    [Fact]
    public async Task SambaLeaseRenewal_ExtendsALiveLease_ButNeverRevivesAnExpiredOne()
    {
        var live = Guid.NewGuid();
        var expired = Guid.NewGuid();
        await using (var db = NewContext())
        {
            foreach (var (id, leaseUntil) in new[] { (live, DateTime.UtcNow.AddMinutes(1)), (expired, DateTime.UtcNow.AddSeconds(-1)) })
                db.SambaLifecycleEventReceipts.Add(new SambaLifecycleEventReceipt
                {
                    EventId = id, EventType = "close", CreatedAtUtc = DateTime.UtcNow,
                    LeaseUntilUtc = leaseUntil, AttemptCount = 1
                });
            await db.SaveChangesAsync();
        }
        var repository = new SambaLifecycleEventRepository(DbFactory);

        Assert.True(await repository.RenewAsync(live, TimeSpan.FromMinutes(2)));
        // An expired lease may already belong to a Samba retry: renewing it would hide the overlap.
        Assert.False(await repository.RenewAsync(expired, TimeSpan.FromMinutes(2)));
    }

    [Theory]
    [InlineData("Host=db;Username=u", true, 30)]
    [InlineData("Host=db;Username=u;Tcp Keepalive=false", false, 0)]
    [InlineData("Host=db;Username=u;tcpkeepalive=true", true, 0)]
    [InlineData("Host=db;Username=u;Tcp Keepalive Time=120", false, 120)]
    public void ConnectionDefaults_AddTcpKeepAliveOnlyWhenTheOperatorDidNotSetIt(
        string configured, bool expectedEnabled, int expectedTime)
    {
        var built = new Npgsql.NpgsqlConnectionStringBuilder(
            ServiceCollectionExtensions.BuildConnectionString(configured, "web"));

        Assert.Equal(expectedEnabled, built.TcpKeepAlive);
        Assert.Equal(expectedTime, built.TcpKeepAliveTime);
        Assert.Equal(0, built.KeepAlive);
        Assert.Equal("kaimo-web", built.ApplicationName);
    }

    [Fact]
    public void StartupWait_TreatsAnExhaustedRetryStrategyAsTransient()
    {
        // With EnableRetryOnFailure, a DB outage inside a strategy-wrapped call surfaces as
        // RetryLimitExceededException (a plain Exception). The startup loops must keep waiting
        // on it instead of letting the process crash.
        var exhausted = new RetryLimitExceededException(
            "Retries exhausted", new Npgsql.NpgsqlException("connection refused", new System.Net.Sockets.SocketException()));

        Assert.True(ServiceCollectionExtensions.IsTransientDatabaseFailure(exhausted));
        Assert.False(ServiceCollectionExtensions.IsTransientDatabaseFailure(
            new SchemaVersionMismatchException("newer schema")));
    }

    /// <summary>Simulates another process inserting the same config key just before our insert.</summary>
    private sealed class InsertSameKeyBeforeFirstSave(TestDbContextFactory factory, string key)
        : SaveChangesInterceptor
    {
        private bool _done;

        public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (!_done)
            {
                _done = true;
                await using var other = factory.CreateDbContext();
                other.ConfigSettings.Add(new ConfigSetting { Key = key, Value = "theirs", UpdatedAt = DateTime.UtcNow });
                await other.SaveChangesAsync(cancellationToken);
            }
            return result;
        }
    }
}

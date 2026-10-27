using System.Text.Json;
using Kaimo_File_Server.Infrastructure.Configuration;
using Kaimo_File_Server.Infrastructure.Services;
using Kaimo_File_Server.Tests.Infrastructure;
using Xunit;

namespace Kaimo_File_Server.Tests;

public sealed class DatabaseCloudSyncOperationCoordinatorTests
    : DatabaseTestBase
{
    [Fact]
    public async Task WedgedLease_BecomesStealable_OnceItsAbsoluteLifetimeCapPasses()
    {
        Guid shareId = Guid.NewGuid();
        var start = new DateTimeOffset(2026, 9, 6, 12, 0, 0, TimeSpan.Zero);
        var time = new MutableTimeProvider(start);
        var coordinator = new DatabaseCloudSyncOperationCoordinator(DbFactory, time);

        // Simulate a stuck sync whose leaked renewal loop keeps pushing ExpiresAt
        // far into the future — the exact condition that otherwise keeps a share
        // "busy or unavailable" until a restart.
        await WriteStuckLeaseAsync(shareId, createdAt: start, expiresAt: start.AddHours(100));

        // Within the absolute cap the lease is still respected, so no one steals it.
        time.Set(start + TimeSpan.FromMinutes(1));
        Assert.Null(await coordinator.TryBeginSyncAsync(shareId, "projects"));

        // Past the cap it is treated as expired despite the future ExpiresAt, so the
        // share recovers on its own.
        time.Set(start + DatabaseCloudSyncOperationCoordinator.LeaseLifetimeCap + TimeSpan.FromMinutes(1));
        var recovered = await coordinator.TryBeginSyncAsync(shareId, "projects");
        Assert.NotNull(recovered);
        await recovered.DisposeAsync();
    }

    private async Task WriteStuckLeaseAsync(
        Guid shareId, DateTimeOffset createdAt, DateTimeOffset expiresAt)
    {
        var payload = new DatabaseCloudSyncOperationCoordinator.LeasePayload(
            Guid.NewGuid(),
            DatabaseCloudSyncOperationCoordinator.LeaseKind.Sync,
            "projects",
            null,
            expiresAt,
            createdAt);
        await using var db = DbFactory.CreateDbContext();
        db.ConfigSettings.Add(new ConfigSetting
        {
            Key = DatabaseCloudSyncOperationCoordinator.GetKey(shareId),
            Value = JsonSerializer.Serialize(payload),
            UpdatedAt = createdAt.UtcDateTime
        });
        await db.SaveChangesAsync();
    }

    private sealed class MutableTimeProvider(DateTimeOffset now) : TimeProvider
    {
        private DateTimeOffset _now = now;
        public override DateTimeOffset GetUtcNow() => _now;
        public void Set(DateTimeOffset value) => _now = value;
    }

    [Fact]
    public async Task Lease_IsVisibleToAnotherCoordinatorInstance()
    {
        Guid shareId = Guid.NewGuid();
        var web = CreateCoordinator();
        var worker = CreateCoordinator();

        var sync = await web.TryBeginSyncAsync(shareId, "projects");
        Assert.NotNull(sync);
        Assert.Null(await worker.TryBeginPathMutationAsync(
            shareId, "unrelated", "renamed"));

        await sync.DisposeAsync();

        var mutation = await worker.TryBeginPathMutationAsync(
            shareId, "unrelated", "renamed");
        Assert.NotNull(mutation);
        await mutation.DisposeAsync();
    }

    [Fact]
    public async Task ExternalReservation_BlocksAnotherProcessUntilCompleted()
    {
        Guid shareId = Guid.NewGuid();
        var smbAuthorization = CreateCoordinator();
        var smbEventHandler = CreateCoordinator();
        var scheduler = CreateCoordinator();

        Assert.True(await smbAuthorization.TryReserveExternalPathMutationAsync(
            shareId, "old", "new", TimeSpan.FromMinutes(2)));
        Assert.Null(await scheduler.TryBeginSyncAsync(shareId, "folder"));

        // The event handler may run in another process. Re-reserving the same
        // operation renews the short authorization-to-event handoff window.
        Assert.True(await smbEventHandler.TryReserveExternalPathMutationAsync(
            shareId, "old", "new", TimeSpan.FromMinutes(2)));
        await smbEventHandler.CompleteExternalPathMutationAsync(
            shareId, "old", "new");

        var sync = await scheduler.TryBeginSyncAsync(shareId, "folder");
        Assert.NotNull(sync);
        await sync.DisposeAsync();
    }

    [Fact]
    public async Task ExternalReservations_ForDifferentPaths_Coexist()
    {
        // Word safe-save: the second rename is authorized before the first one's
        // NotifyRename has completed its reservation.
        Guid shareId = Guid.NewGuid();
        var authorization = CreateCoordinator();
        var eventHandler = CreateCoordinator();
        var scheduler = CreateCoordinator();

        Assert.True(await authorization.TryReserveExternalPathMutationAsync(
            shareId, "doc.docx", "~WRL0001.tmp", TimeSpan.FromMinutes(2)));
        Assert.True(await authorization.TryReserveExternalPathMutationAsync(
            shareId, "~WRD0000.tmp", "doc.docx", TimeSpan.FromMinutes(2)));

        // The first event renews and completes its own reservation only.
        Assert.True(await eventHandler.TryReserveExternalPathMutationAsync(
            shareId, "doc.docx", "~WRL0001.tmp", TimeSpan.FromMinutes(2)));
        await eventHandler.CompleteExternalPathMutationAsync(
            shareId, "doc.docx", "~WRL0001.tmp");
        Assert.Null(await scheduler.TryBeginSyncAsync(shareId, "folder"));

        await eventHandler.CompleteExternalPathMutationAsync(
            shareId, "~WRD0000.tmp", "doc.docx");
        var sync = await scheduler.TryBeginSyncAsync(shareId, "folder");
        Assert.NotNull(sync);

        // A running sync still blocks SMB renames.
        Assert.False(await authorization.TryReserveExternalPathMutationAsync(
            shareId, "a", "b", TimeSpan.FromMinutes(2)));
        await sync.DisposeAsync();
    }

    [Fact]
    public async Task ExpiredExternalReservation_DoesNotKeepTheShareBusy()
    {
        Guid shareId = Guid.NewGuid();
        var start = new DateTimeOffset(2026, 10, 27, 12, 0, 0, TimeSpan.Zero);
        var time = new MutableTimeProvider(start);
        var coordinator = new DatabaseCloudSyncOperationCoordinator(DbFactory, time);

        Assert.True(await coordinator.TryReserveExternalPathMutationAsync(
            shareId, "lost", "event", TimeSpan.FromMinutes(2)));
        time.Set(start + TimeSpan.FromMinutes(1));
        Assert.True(await coordinator.TryReserveExternalPathMutationAsync(
            shareId, "a", "b", TimeSpan.FromMinutes(2)));
        await coordinator.CompleteExternalPathMutationAsync(shareId, "a", "b");

        // "lost" never completes; once it expires the share is free again.
        time.Set(start + TimeSpan.FromMinutes(3));
        var sync = await coordinator.TryBeginSyncAsync(shareId, "folder");
        Assert.NotNull(sync);
        await sync.DisposeAsync();
    }

    private DatabaseCloudSyncOperationCoordinator CreateCoordinator()
        => new(DbFactory, TimeProvider.System);
}

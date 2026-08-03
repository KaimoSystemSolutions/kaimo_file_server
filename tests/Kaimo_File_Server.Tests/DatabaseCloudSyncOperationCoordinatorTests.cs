using Kaimo_File_Server.Infrastructure.Services;
using Kaimo_File_Server.Tests.Infrastructure;
using Xunit;

namespace Kaimo_File_Server.Tests;

public sealed class DatabaseCloudSyncOperationCoordinatorTests
    : DatabaseTestBase
{
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

    private DatabaseCloudSyncOperationCoordinator CreateCoordinator()
        => new(DbFactory, TimeProvider.System);
}

using Kaimo_File_Server.Core.Services;
using Xunit;

namespace Kaimo_File_Server.Tests;

public sealed class CloudSyncOperationCoordinatorTests
{
    private readonly InMemoryCloudSyncOperationCoordinator _sut = new(TimeProvider.System);
    private readonly Guid _shareId = Guid.NewGuid();

    [Fact]
    public async Task ActiveSync_BlocksEveryMutationInSameShare()
    {
        var syncLease = await _sut.TryBeginSyncAsync(_shareId, "projects");
        Assert.NotNull(syncLease);
        await using (syncLease)
        {
            Assert.Null(await _sut.TryBeginPathMutationAsync(
                _shareId, "projects/customer", "archive/customer"));
            Assert.Null(await _sut.TryBeginPathMutationAsync(
                _shareId, "unrelated", "other"));
        }

        var after = await _sut.TryBeginPathMutationAsync(
            _shareId, "projects/customer", "archive/customer");
        Assert.NotNull(after);
        await after.DisposeAsync();
    }

    [Fact]
    public async Task ActiveSync_BlocksWholeShareMutation()
    {
        var syncLease = await _sut.TryBeginSyncAsync(_shareId, "folder");
        Assert.NotNull(syncLease);
        await using (syncLease)
            Assert.Null(await _sut.TryBeginShareMutationAsync(_shareId));

        var after = await _sut.TryBeginShareMutationAsync(_shareId);
        Assert.NotNull(after);
        await after.DisposeAsync();
    }

    [Fact]
    public async Task ActiveMutation_BlocksEverySyncInShareUntilLeaseIsReleased()
    {
        var mutationLease = await _sut.TryBeginPathMutationAsync(
            _shareId, "photos/old", "photos/new");
        Assert.NotNull(mutationLease);
        await using (mutationLease)
        {
            Assert.Null(await _sut.TryBeginSyncAsync(_shareId, "photos"));
            Assert.Null(await _sut.TryBeginSyncAsync(
                _shareId, "documents"));
        }

        var after = await _sut.TryBeginSyncAsync(_shareId, "photos");
        Assert.NotNull(after);
        await after.DisposeAsync();
    }

    [Fact]
    public async Task ExternalReservation_BlocksTimerStyleSyncUntilEventCompletes()
    {
        Assert.True(await _sut.TryReserveExternalPathMutationAsync(
            _shareId, "media", "archive/media", TimeSpan.FromMinutes(2)));
        Assert.Null(await _sut.TryBeginSyncAsync(_shareId, "media/videos"));

        await _sut.CompleteExternalPathMutationAsync(
            _shareId, "media", "archive/media");

        var after = await _sut.TryBeginSyncAsync(
            _shareId, "archive/media/videos");
        Assert.NotNull(after);
        await after.DisposeAsync();
    }

    [Fact]
    public async Task OverlappingSyncs_AreSerializedAcrossDifferentStarters()
    {
        var first = await _sut.TryBeginSyncAsync(_shareId, "team");
        Assert.NotNull(first);
        await using (first)
        {
            Assert.Null(await _sut.TryBeginSyncAsync(_shareId, "team"));
            Assert.Null(await _sut.TryBeginSyncAsync(_shareId, "team/sub"));
        }
    }
}

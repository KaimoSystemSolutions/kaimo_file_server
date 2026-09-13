using Kaimo_File_Server.Core.Services.DataServices;
using Kaimo_File_Server.Web.Components.ViewModels;
using Xunit;

namespace Kaimo_File_Server.Tests;

public sealed class SyncItemStateEvaluatorTests
{
    private static readonly DateTime LastRun = new(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);

    [Theory]
    [InlineData(SyncMode.Push)]
    [InlineData(SyncMode.TwoWay)]
    public void WrittenBeforeLastRun_UnderPushOrTwoWay_IsSynced(SyncMode mode)
    {
        var state = SyncItemStateEvaluator.Evaluate(
            LastRun.AddMinutes(-5), LastRun, mode, isRemoteBacked: null);
        Assert.Equal(SyncItemState.Synced, state);
    }

    [Theory]
    [InlineData(SyncMode.Push)]
    [InlineData(SyncMode.TwoWay)]
    public void ChangedAfterLastRun_UnderPushOrTwoWay_IsPendingUpload(SyncMode mode)
    {
        var state = SyncItemStateEvaluator.Evaluate(
            LastRun.AddMinutes(5), LastRun, mode, isRemoteBacked: null);
        Assert.Equal(SyncItemState.PendingUpload, state);
    }

    [Fact]
    public void NoSuccessfulRunYet_UnderPush_TreatsItemAsPending()
    {
        var state = SyncItemStateEvaluator.Evaluate(
            LastRun, lastSuccessfulRunAtUtc: null, SyncMode.Push, isRemoteBacked: null);
        Assert.Equal(SyncItemState.PendingUpload, state);
    }

    [Fact]
    public void Pull_LocalOnlyItem_IsPullBlocked_RegardlessOfRunTime()
    {
        // Manifest is known and the item is absent from it: pull never uploads it,
        // so a run (however recent) must not clear the flag.
        var state = SyncItemStateEvaluator.Evaluate(
            LastRun.AddMinutes(-30), LastRun, SyncMode.Pull, isRemoteBacked: false);
        Assert.Equal(SyncItemState.PullBlocked, state);
    }

    [Fact]
    public void Pull_RemoteBackedItem_IsSynced()
    {
        var state = SyncItemStateEvaluator.Evaluate(
            LastRun.AddMinutes(5), LastRun, SyncMode.Pull, isRemoteBacked: true);
        Assert.Equal(SyncItemState.Synced, state);
    }

    [Fact]
    public void Pull_NoManifestYet_FallsBackToTimestamp_OldItemIsSynced()
    {
        // No manifest recorded (isRemoteBacked null): don't flag everything — an item
        // written before the last run reads as synced, as it did before manifest tracking.
        var state = SyncItemStateEvaluator.Evaluate(
            LastRun.AddMinutes(-5), LastRun, SyncMode.Pull, isRemoteBacked: null);
        Assert.Equal(SyncItemState.Synced, state);
    }

    [Fact]
    public void Pull_NoManifestYet_FallsBackToTimestamp_NewItemIsPullBlocked()
    {
        var state = SyncItemStateEvaluator.Evaluate(
            LastRun.AddMinutes(5), LastRun, SyncMode.Pull, isRemoteBacked: null);
        Assert.Equal(SyncItemState.PullBlocked, state);
    }
}

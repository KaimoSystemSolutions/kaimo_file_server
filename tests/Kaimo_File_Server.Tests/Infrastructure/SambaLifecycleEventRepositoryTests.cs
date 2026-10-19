using Kaimo_File_Server.Core.Repositories;
using Kaimo_File_Server.Infrastructure.Repositories;
using Kaimo_File_Server.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Kaimo_File_Server.Tests;

public sealed class SambaLifecycleEventRepositoryTests : DatabaseTestBase
{
    [Fact]
    public async Task Receipt_LeasesRetriesCompletesAndDeduplicates()
    {
        var repository = new SambaLifecycleEventRepository(DbFactory);
        var eventId = Guid.NewGuid();

        var first = await repository.TryClaimAsync(
            eventId, "close", TimeSpan.FromMinutes(1));
        Assert.Equal(new SambaEventClaim(SambaEventClaimResult.Acquired, 1), first);
        Assert.Equal(
            SambaEventClaimResult.Busy,
            (await repository.TryClaimAsync(
                eventId, "close", TimeSpan.FromMinutes(1))).Result);
        Assert.Equal(
            SambaEventClaimResult.Conflict,
            (await repository.TryClaimAsync(
                eventId, "rename", TimeSpan.FromMinutes(1))).Result);

        await repository.ReleaseAsync(eventId, first.Attempt, "temporary bridge failure");
        var retry = await repository.TryClaimAsync(
            eventId, "close", TimeSpan.FromMinutes(1));
        Assert.Equal(new SambaEventClaim(SambaEventClaimResult.Acquired, 2), retry);

        await repository.CompleteAsync(eventId);
        Assert.Equal(
            SambaEventClaimResult.AlreadyCompleted,
            (await repository.TryClaimAsync(
                eventId, "close", TimeSpan.FromMinutes(1))).Result);

        using var db = NewContext();
        var receipt = await db.SambaLifecycleEventReceipts.FindAsync(eventId);
        Assert.NotNull(receipt);
        Assert.Equal(2, receipt.AttemptCount);
        Assert.NotNull(receipt.CompletedAtUtc);
        Assert.Null(receipt.LeaseUntilUtc);
        Assert.Null(receipt.LastError);
    }

    [Fact]
    public async Task ReclaimedEvent_IsNeitherReleasedNorRenewedByTheFormerOwner()
    {
        var repository = new SambaLifecycleEventRepository(DbFactory);
        var eventId = Guid.NewGuid();
        var first = await repository.TryClaimAsync(
            eventId, "close", TimeSpan.FromMinutes(1));
        // The first handler's lease runs out and a Samba retry re-claims the event.
        await using (var db = NewContext())
            await db.SambaLifecycleEventReceipts
                .Where(x => x.EventId == eventId)
                .ExecuteUpdateAsync(setters => setters.SetProperty(x => x.LeaseUntilUtc, DateTime.UtcNow.AddSeconds(-1)));
        var second = await repository.TryClaimAsync(
            eventId, "close", TimeSpan.FromMinutes(1));
        Assert.Equal(SambaEventClaimResult.Acquired, second.Result);

        // Before the fix the former owner expired the new claim, letting a further retry start
        // alongside the running handler.
        await repository.ReleaseAsync(eventId, first.Attempt, "late failure");
        Assert.False(await repository.RenewAsync(eventId, first.Attempt, TimeSpan.FromMinutes(1)));

        using (var assertionDb = NewContext())
        {
            var receipt = await assertionDb.SambaLifecycleEventReceipts.FindAsync(eventId);
            Assert.NotNull(receipt);
            Assert.Null(receipt.LastError);
            Assert.True(receipt.LeaseUntilUtc > DateTime.UtcNow);
        }
        Assert.True(await repository.RenewAsync(eventId, second.Attempt, TimeSpan.FromMinutes(1)));
    }

    [Fact]
    public async Task CompletedReceipt_RetentionDeletionIsBoundedByCutoff()
    {
        var repository = new SambaLifecycleEventRepository(DbFactory);
        var eventId = Guid.NewGuid();
        await repository.TryClaimAsync(
            eventId, "delete", TimeSpan.FromMinutes(1));
        await repository.CompleteAsync(eventId);

        Assert.Equal(
            0,
            await repository.DeleteCompletedBeforeAsync(
                DateTime.UtcNow.AddDays(-1)));
        Assert.Equal(
            1,
            await repository.DeleteCompletedBeforeAsync(
                DateTime.UtcNow.AddMinutes(1)));
    }
}

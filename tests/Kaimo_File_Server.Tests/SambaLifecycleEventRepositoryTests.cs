using Kaimo_File_Server.Core.Repositories;
using Kaimo_File_Server.Infrastructure.Repositories;
using Kaimo_File_Server.Tests.Infrastructure;
using Xunit;

namespace Kaimo_File_Server.Tests;

public sealed class SambaLifecycleEventRepositoryTests : DatabaseTestBase
{
    [Fact]
    public async Task Receipt_LeasesRetriesCompletesAndDeduplicates()
    {
        var repository = new SambaLifecycleEventRepository(DbFactory);
        var eventId = Guid.NewGuid();

        Assert.Equal(
            SambaEventClaimResult.Acquired,
            await repository.TryClaimAsync(
                eventId, "close", TimeSpan.FromMinutes(1)));
        Assert.Equal(
            SambaEventClaimResult.Busy,
            await repository.TryClaimAsync(
                eventId, "close", TimeSpan.FromMinutes(1)));
        Assert.Equal(
            SambaEventClaimResult.Conflict,
            await repository.TryClaimAsync(
                eventId, "rename", TimeSpan.FromMinutes(1)));

        await repository.ReleaseAsync(eventId, "temporary bridge failure");
        Assert.Equal(
            SambaEventClaimResult.Acquired,
            await repository.TryClaimAsync(
                eventId, "close", TimeSpan.FromMinutes(1)));

        await repository.CompleteAsync(eventId);
        Assert.Equal(
            SambaEventClaimResult.AlreadyCompleted,
            await repository.TryClaimAsync(
                eventId, "close", TimeSpan.FromMinutes(1)));

        using var db = NewContext();
        var receipt = await db.SambaLifecycleEventReceipts.FindAsync(eventId);
        Assert.NotNull(receipt);
        Assert.Equal(2, receipt.AttemptCount);
        Assert.NotNull(receipt.CompletedAtUtc);
        Assert.Null(receipt.LeaseUntilUtc);
        Assert.Null(receipt.LastError);
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

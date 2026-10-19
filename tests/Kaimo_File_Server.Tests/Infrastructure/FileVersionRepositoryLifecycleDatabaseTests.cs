using Kaimo_File_Server.Core.Domain;
using Kaimo_File_Server.Infrastructure.Repositories;
using Kaimo_File_Server.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Kaimo_File_Server.Tests;

public class FileVersionRepositoryLifecycleDatabaseTests : DatabaseTestBase
{
    private static FileVersion Version(Guid shareId, string path, string hash, int number = 1)
        => new(
            shareId, path, DateTime.UtcNow.AddSeconds(number),
            $"AA/BB/{hash}.bin.gz", hash, 10, null, number);

    [Fact]
    public async Task RenamePathAsync_MovesSubtreeAndRemovesDisplacedDestination()
    {
        var shareId = Guid.NewGuid();
        var source = Version(shareId, "old/a.txt", "SOURCE");
        var nested = Version(shareId, "old/nested/b.txt", "NESTED", 2);
        var displaced = Version(shareId, "new/a.txt", "TARGET", 3);
        await using (var db = NewContext())
        {
            db.FileVersions.AddRange(source, nested, displaced);
            await db.SaveChangesAsync();
        }

        var removed = await new FileVersionRepository(DbFactory)
            .RenamePathAsync(shareId, "old", "new");

        Assert.Contains(removed, v => v.Id == displaced.Id);
        await using var assertionDb = NewContext();
        Assert.False(await assertionDb.FileVersions.AnyAsync(v => v.Id == displaced.Id));
        Assert.Equal("new/a.txt", (await assertionDb.FileVersions.FindAsync(source.Id))!.FilePath);
        Assert.Equal("new/nested/b.txt", (await assertionDb.FileVersions.FindAsync(nested.Id))!.FilePath);
    }

    [Fact]
    public async Task SambaRenameRetry_PreservesVersionsCreatedAfterAtomicCheckpoint()
    {
        var shareId = Guid.NewGuid();
        var eventId = Guid.NewGuid();
        var displaced = Version(shareId, "new.txt", "DISPLACED");
        await using (var db = NewContext())
        {
            db.FileVersions.Add(displaced);
            db.SambaLifecycleEventReceipts.Add(new SambaLifecycleEventReceipt
            {
                EventId = eventId,
                EventType = "rename",
                CreatedAtUtc = DateTime.UtcNow,
                LeaseUntilUtc = DateTime.UtcNow.AddMinutes(1),
                AttemptCount = 1
            });
            await db.SaveChangesAsync();
        }

        var removed = await new FileVersionRepository(DbFactory)
            .RenamePathAsync(shareId, "old.txt", "new.txt", eventId);
        Assert.Contains(removed, version => version.Id == displaced.Id);

        var laterVersion = Version(shareId, "new.txt", "LATER", 2);
        await using (var db = NewContext())
        {
            db.FileVersions.Add(laterVersion);
            await db.SaveChangesAsync();
        }

        var retryRemoved = await new FileVersionRepository(DbFactory)
            .RenamePathAsync(shareId, "old.txt", "new.txt", eventId);

        Assert.Empty(retryRemoved);
        await using var assertionDb = NewContext();
        Assert.NotNull(await assertionDb.FileVersions.FindAsync(laterVersion.Id));
        var receipt = await assertionDb.SambaLifecycleEventReceipts.FindAsync(eventId);
        Assert.NotNull(receipt!.RenameVersionsCompletedAtUtc);
    }

    [Fact]
    public async Task DeletePathAsync_UsesSegmentBoundaryAndReturnsRemovedRows()
    {
        var shareId = Guid.NewGuid();
        await using (var db = NewContext())
        {
            db.FileVersions.AddRange(
                Version(shareId, "folder/a.txt", "A"),
                Version(shareId, "folder/nested/b.txt", "B", 2),
                Version(shareId, "folder-other/keep.txt", "KEEP", 3));
            await db.SaveChangesAsync();
        }

        var removed = await new FileVersionRepository(DbFactory)
            .DeletePathAsync(shareId, "folder");

        Assert.Equal(2, removed.Count);
        await using var assertionDb = NewContext();
        Assert.Single(await assertionDb.FileVersions.ToListAsync());
        Assert.Equal("folder-other/keep.txt", (await assertionDb.FileVersions.SingleAsync()).FilePath);
    }
}

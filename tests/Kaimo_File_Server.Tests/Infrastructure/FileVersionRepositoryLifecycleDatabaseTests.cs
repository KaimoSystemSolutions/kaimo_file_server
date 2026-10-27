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
    public async Task RenamePathAsync_MovesSubtreeAndMergesDestinationHistory()
    {
        var shareId = Guid.NewGuid();
        var replaced = Version(shareId, "new/a.txt", "TARGET", 1);
        var source = Version(shareId, "old/a.txt", "SOURCE", 2);
        var nested = Version(shareId, "old/nested/b.txt", "NESTED", 3);
        await using (var db = NewContext())
        {
            db.FileVersions.AddRange(source, nested, replaced);
            await db.SaveChangesAsync();
        }

        var removed = await new FileVersionRepository(DbFactory)
            .RenamePathAsync(shareId, "old", "new");

        // The replaced file's content stays recoverable as an older version of the path.
        Assert.Empty(removed);
        await using var assertionDb = NewContext();
        Assert.Equal("new/a.txt", (await assertionDb.FileVersions.FindAsync(replaced.Id))!.FilePath);
        Assert.Equal(1, (await assertionDb.FileVersions.FindAsync(replaced.Id))!.VersionNumber);
        Assert.Equal("new/a.txt", (await assertionDb.FileVersions.FindAsync(source.Id))!.FilePath);
        Assert.Equal(2, (await assertionDb.FileVersions.FindAsync(source.Id))!.VersionNumber);
        Assert.Equal("new/nested/b.txt", (await assertionDb.FileVersions.FindAsync(nested.Id))!.FilePath);
    }

    [Fact]
    public async Task RenamePathAsync_SameSecondAtDestination_DisplacesOnlyThatRow()
    {
        var shareId = Guid.NewGuid();
        var at = new DateTime(2026, 5, 1, 8, 0, 0, DateTimeKind.Utc);
        var source = new FileVersion(shareId, "lu1.tmp", at, "AA/BB/S.bin.gz", "S", 10, null, 1);
        var clash = new FileVersion(shareId, "doc.odt", at, "AA/BB/C.bin.gz", "C", 10, null, 2);
        var older = new FileVersion(shareId, "doc.odt", at.AddMinutes(-1), "AA/BB/O.bin.gz", "O", 10, null, 1);
        await using (var db = NewContext())
        {
            db.FileVersions.AddRange(source, clash, older);
            await db.SaveChangesAsync();
        }

        var removed = await new FileVersionRepository(DbFactory)
            .RenamePathAsync(shareId, "lu1.tmp", "doc.odt");

        Assert.Equal(clash.Id, Assert.Single(removed).Id);
        await using var assertionDb = NewContext();
        var history = await assertionDb.FileVersions.OrderBy(v => v.SnapshotTimestampUtc).ToListAsync();
        Assert.Equal([older.Id, source.Id], history.Select(v => v.Id));
        Assert.All(history, v => Assert.Equal("doc.odt", v.FilePath));
        Assert.Equal([1, 2], history.Select(v => v.VersionNumber));
    }

    [Fact]
    public async Task RenamePathAsync_OlderFileOverNewerOne_InsertsLiveCopyOnce()
    {
        var shareId = Guid.NewGuid();
        var at = new DateTime(2026, 5, 1, 8, 0, 0, DateTimeKind.Utc);
        var older = new FileVersion(shareId, "a.txt", at, "AA/BB/A.bin.gz", "A", 10, null, 1);
        var newer = new FileVersion(shareId, "b.txt", at.AddMinutes(1), "AA/BB/B.bin.gz", "B", 10, null, 1);
        await using (var db = NewContext())
        {
            db.FileVersions.AddRange(older, newer);
            await db.SaveChangesAsync();
        }

        await new FileVersionRepository(DbFactory).RenamePathAsync(shareId, "a.txt", "b.txt");

        await using var assertionDb = NewContext();
        var history = await assertionDb.FileVersions
            .Where(v => v.FilePath == "b.txt").OrderByDescending(v => v.SnapshotTimestampUtc).ToListAsync();
        Assert.Equal(["A", "B", "A"], history.Select(v => v.ContentHash));
        Assert.Equal([3, 2, 1], history.Select(v => v.VersionNumber));
    }

    [Fact]
    public async Task MissingSince_ReplaceAndReadBack_PerShare()
    {
        var shareA = Guid.NewGuid();
        var shareB = Guid.NewGuid();
        var at = new DateTime(2026, 5, 1, 8, 0, 0, DateTimeKind.Utc);
        var repo = new FileVersionRepository(DbFactory);

        await repo.ReplaceMissingSinceAsync(shareA, new Dictionary<string, DateTime> { ["a.txt"] = at, ["b.txt"] = at });
        await repo.ReplaceMissingSinceAsync(shareB, new Dictionary<string, DateTime> { ["a.txt"] = at.AddDays(1) });
        // A later pass: b.txt is back, c.txt went missing, a.txt keeps its first time.
        await repo.ReplaceMissingSinceAsync(shareA, new Dictionary<string, DateTime> { ["a.txt"] = at, ["c.txt"] = at.AddHours(6) });

        var missingA = await repo.GetMissingSinceAsync(shareA);
        Assert.Equal(2, missingA.Count);
        Assert.Equal(at, missingA["a.txt"]);
        Assert.Equal(at.AddHours(6), missingA["c.txt"]);
        Assert.Equal(at.AddDays(1), (await repo.GetMissingSinceAsync(shareB))["a.txt"]);

        await repo.ReplaceMissingSinceAsync(shareA, new Dictionary<string, DateTime>());
        Assert.Empty(await repo.GetMissingSinceAsync(shareA));
        Assert.Single(await repo.GetMissingSinceAsync(shareB));
    }

    [Fact]
    public async Task SambaRenameRetry_PreservesVersionsCreatedAfterAtomicCheckpoint()
    {
        var shareId = Guid.NewGuid();
        var eventId = Guid.NewGuid();
        var replaced = Version(shareId, "new.txt", "REPLACED");
        var source = Version(shareId, "old.txt", "SOURCE", 2);
        await using (var db = NewContext())
        {
            db.FileVersions.AddRange(replaced, source);
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
        Assert.Empty(removed);

        var laterVersion = Version(shareId, "new.txt", "LATER", 3);
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
        Assert.Equal(3, await assertionDb.FileVersions.CountAsync(v => v.FilePath == "new.txt"));
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

using Kaimo_File_Server.Core.Domain;
using Kaimo_File_Server.Infrastructure.Repositories;
using Kaimo_File_Server.Tests.Infrastructure;
using Xunit;

namespace Kaimo_File_Server.Tests;

/// <summary>The repository queries behind the retention sweep, against the real EF schema.</summary>
public class FileVersionRepositoryRetentionDatabaseTests : DatabaseTestBase
{
    private static readonly DateTime T0 = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    private static FileVersion Version(Guid shareId, string path, int minute, string? blob = null)
        => new(shareId, path, T0.AddMinutes(minute), $"AA/BB/{blob ?? Guid.NewGuid().ToString("N")}.bin.gz",
            "HASH", 10, null, minute);

    private async Task SeedAsync(params FileVersion[] versions)
    {
        await using var db = NewContext();
        db.FileVersions.AddRange(versions);
        await db.SaveChangesAsync();
    }

    private FileVersionRepository Repo() => new(DbFactory);

    [Fact]
    public async Task DeleteByIdsAsync_RemovesOnlyTheGivenRows()
    {
        var share = Guid.NewGuid();
        var a = Version(share, "a.txt", 1);
        var b = Version(share, "a.txt", 2);
        await SeedAsync(a, b);

        var removed = await Repo().DeleteByIdsAsync([a.Id, Guid.NewGuid()]);

        Assert.Equal([a.Id], removed.Select(v => v.Id));
        Assert.Equal([b.Id], (await Repo().GetVersionsAsync(share, "a.txt")).Select(v => v.Id));
    }

    [Fact]
    public async Task GetShareIdsWithVersionsAsync_ReturnsDistinctShares()
    {
        var s1 = Guid.NewGuid();
        var s2 = Guid.NewGuid();
        await SeedAsync(Version(s1, "a", 1), Version(s1, "b", 2), Version(s2, "a", 3), Version(Guid.Empty, "x", 4));

        var ids = await Repo().GetShareIdsWithVersionsAsync();

        Assert.Equal(3, ids.Count);
        Assert.Contains(Guid.Empty, ids);
    }

    [Fact]
    public async Task GetVersionedPathsPageAsync_PagesDistinctPathsInOrder()
    {
        var share = Guid.NewGuid();
        await SeedAsync(
            Version(share, "c.txt", 1), Version(share, "a.txt", 2), Version(share, "a.txt", 3),
            Version(share, "b/x.txt", 4), Version(share, "d.txt", 5), Version(Guid.NewGuid(), "zz.txt", 6));

        var first = await Repo().GetVersionedPathsPageAsync(share, null, 2);
        var second = await Repo().GetVersionedPathsPageAsync(share, first[^1], 2);
        var third = await Repo().GetVersionedPathsPageAsync(share, second[^1], 2);

        Assert.Equal(["a.txt", "b/x.txt"], first);
        Assert.Equal(["c.txt", "d.txt"], second);
        Assert.Empty(third);
    }

    [Fact]
    public async Task GetBudgetPruneCandidatesAsync_ExcludesNewestPerFileAndOrdersOldestFirst()
    {
        var share = Guid.NewGuid();
        var other = Guid.NewGuid();
        var aOld = Version(share, "a.txt", 1);
        var aNew = Version(share, "a.txt", 5);
        var bOld = Version(share, "b.txt", 0);
        var bMid = Version(share, "b.txt", 3);
        var bNew = Version(share, "b.txt", 9);
        var single = Version(share, "c.txt", -10);
        var foreign = Version(other, "a.txt", -20);
        var foreignNew = Version(other, "a.txt", 20);
        await SeedAsync(aOld, aNew, bOld, bMid, bNew, single, foreign, foreignNew);

        var candidates = await Repo().GetBudgetPruneCandidatesAsync([share], 10);

        Assert.Equal([bOld.Id, aOld.Id, bMid.Id], candidates.Select(v => v.Id));
    }

    [Fact]
    public async Task GetBudgetPruneCandidatesAsync_HonoursTake()
    {
        var share = Guid.NewGuid();
        await SeedAsync(Version(share, "a", 1), Version(share, "a", 2), Version(share, "a", 3));

        Assert.Single(await Repo().GetBudgetPruneCandidatesAsync([share], 1));
        Assert.Empty(await Repo().GetBudgetPruneCandidatesAsync([], 10));
    }
}

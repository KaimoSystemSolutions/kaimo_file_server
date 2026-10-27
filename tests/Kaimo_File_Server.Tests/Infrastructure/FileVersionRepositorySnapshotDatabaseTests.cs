using Kaimo_File_Server.Core.Domain;
using Kaimo_File_Server.Infrastructure.Persistence;
using Kaimo_File_Server.Infrastructure.Repositories;
using Kaimo_File_Server.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Kaimo_File_Server.Tests;

/// <summary>
/// Point-in-time folder queries (newest version per file, snapshot timestamps), run as SQL
/// against the real EF schema. Shared by the Sqlite and the PostgreSQL test classes.
/// </summary>
internal static class FileVersionSnapshotQueryCases
{
    private static readonly DateTime T0 = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    public static FileVersion Version(Guid shareId, string path, int minute)
        => new(shareId, path, T0.AddMinutes(minute), $"AA/BB/{Guid.NewGuid():N}.bin.gz", "HASH", 10, null, minute);

    public static async Task SeedAsync(IDbContextFactory<ApplicationDbContext> factory,
        params FileVersion[] versions)
    {
        await using var db = await factory.CreateDbContextAsync();
        db.FileVersions.AddRange(versions);
        await db.SaveChangesAsync();
    }

    public static async Task LatestVersionsUnderPrefix_PicksNewestPerFileAtOrBeforeCutoff(FileVersionRepository repo,
        IDbContextFactory<ApplicationDbContext> factory)
    {
        var share = Guid.NewGuid();
        var a1 = Version(share, "docs/a.txt", 1);
        var a5 = Version(share, "docs/a.txt", 5);
        var b2 = Version(share, "docs/B.txt", 2);
        await SeedAsync(factory,
            a1, a5, Version(share, "docs/a.txt", 9), b2,
            Version(share, "docs/later.txt", 7),      // created after the cutoff
            Version(share, "docs2/x.txt", 1),         // sibling folder
            Version(Guid.NewGuid(), "docs/a.txt", 4)); // other share

        var snapshot = await repo.GetLatestVersionsUnderPrefixAsync(share, "docs/", T0.AddMinutes(6));

        Assert.Equal([a5.Id, b2.Id], snapshot.Select(v => v.Id));
    }

    public static async Task AllSnapshotTimestamps_LimitReturnsNewestDistinct(FileVersionRepository repo,
        IDbContextFactory<ApplicationDbContext> factory)
    {
        var share = Guid.NewGuid();
        await SeedAsync(factory,
            Version(share, "docs/a.txt", 1), Version(share, "docs/a.txt", 2),
            Version(share, "docs/b.txt", 2), Version(share, "docs/b.txt", 3),
            Version(share, "other/c.txt", 4));

        var limited = await repo.GetAllSnapshotTimestampsAsync(share, "docs/", limit: 2);
        var all = await repo.GetAllSnapshotTimestampsAsync(share, "docs/");

        Assert.Equal([T0.AddMinutes(3), T0.AddMinutes(2)], limited);
        Assert.Equal([T0.AddMinutes(3), T0.AddMinutes(2), T0.AddMinutes(1)], all);
    }
}

public class FileVersionRepositorySnapshotDatabaseTests : DatabaseTestBase
{
    [Fact]
    public Task GetLatestVersionsUnderPrefixAsync_PicksNewestPerFileAtOrBeforeCutoff()
        => FileVersionSnapshotQueryCases.LatestVersionsUnderPrefix_PicksNewestPerFileAtOrBeforeCutoff(new(DbFactory), DbFactory);

    [Fact]
    public Task GetAllSnapshotTimestampsAsync_LimitReturnsNewestDistinct()
        => FileVersionSnapshotQueryCases.AllSnapshotTimestamps_LimitReturnsNewestDistinct(new(DbFactory), DbFactory);
}

public sealed class FileVersionRepositorySnapshotPostgresTests : PostgresTestBase
{
    [PostgresFact]
    public Task GetLatestVersionsUnderPrefixAsync_PicksNewestPerFileAtOrBeforeCutoff()
        => FileVersionSnapshotQueryCases.LatestVersionsUnderPrefix_PicksNewestPerFileAtOrBeforeCutoff(new(DbFactory), DbFactory);

    [PostgresFact]
    public Task GetAllSnapshotTimestampsAsync_LimitReturnsNewestDistinct()
        => FileVersionSnapshotQueryCases.AllSnapshotTimestamps_LimitReturnsNewestDistinct(new(DbFactory), DbFactory);
}

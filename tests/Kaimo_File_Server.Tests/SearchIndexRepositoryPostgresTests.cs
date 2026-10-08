using System.Security.Cryptography;
using System.Text;
using Kaimo_File_Server.Infrastructure.Repositories;
using Kaimo_File_Server.Search;
using Kaimo_File_Server.Tests.Infrastructure;
using Xunit;

namespace Kaimo_File_Server.Tests;

/// <summary>
/// The local search index against real PostgreSQL (pg_trgm, md5(), ILIKE, LIKE escaping).
/// Skipped unless <c>KAIMO_TEST_PG</c> is set.
/// </summary>
public sealed class SearchIndexRepositoryPostgresTests : PostgresTestBase
{
    private static readonly string Root = Path.Combine(Path.GetTempPath(), "share");
    private static readonly char Sep = Path.DirectorySeparatorChar;
    private static readonly DateTime T0 = new(2026, 10, 1, 12, 0, 0, DateTimeKind.Utc);

    private SearchIndexRepository Repo => new(DbFactory);

    private static string Abs(string rel) => Path.Combine(Root, rel.Replace('/', Sep));

    private static string Md5(string s)
        => Convert.ToHexString(MD5.HashData(Encoding.UTF8.GetBytes(s))).ToLowerInvariant();

    private static FileDocument Doc(string rel, string content = "", bool dir = false, string share = "Team")
        => new()
        {
            Id = Md5(Abs(rel)),
            FileName = Path.GetFileName(rel),
            ShareName = share,
            AbsolutePath = Abs(rel),
            SharePath = rel,
            Content = content,
            FileType = dir ? string.Empty : Path.GetExtension(rel).TrimStart('.'),
            IsDirectory = dir,
            Created = T0
        };

    private Task Put(string rel, string content = "", bool dir = false, string share = "Team")
        => Repo.UpsertAsync(Doc(rel, content, dir, share), T0, T0);

    private Task<List<FileDocument>> Find(params string[] tokens)
        => Repo.SearchAsync(tokens, null, null, 0, 50);

    [PostgresFact]
    public async Task Search_RanksNameAboveContent_AndFindsSubstringsInBoth()
    {
        await Put("docs/notes.txt", "the quarterly report is attached");
        await Put("docs/report-2026.txt", "nothing relevant");
        await Put("docs/other.txt", "unrelated");

        var hits = await Find("report");

        Assert.Equal(new[] { "report-2026.txt", "notes.txt" }, hits.Select(h => h.FileName));
        Assert.Contains("report", hits[1].HighlightSnippet);
        Assert.Equal(new[] { "notes.txt" }, (await Find("arterl")).Select(h => h.FileName));
    }

    [PostgresFact]
    public async Task Search_TwoCharTerm_MatchesNamesOnly()
    {
        await Put("ab-name.txt", "zz");
        await Put("other.txt", "contains ab inside");

        Assert.Equal(new[] { "ab-name.txt" }, (await Find("ab")).Select(h => h.FileName));
    }

    [PostgresFact]
    public async Task Search_ScopesToShareAndFolderWithoutSiblingsOrWildcards()
    {
        await Put("reports/a.txt", "budget");
        await Put("reports2/b.txt", "budget");
        await Put("re_orts/c.txt", "budget");
        await Put("reports/z.txt", "budget", share: "Other");

        var scoped = await Repo.SearchAsync(new[] { "budget" }, "Team", "reports", 0, 50);
        var underscore = await Repo.SearchAsync(new[] { "budget" }, "Team", "re_orts", 0, 50);

        Assert.Equal(new[] { "a.txt" }, scoped.Select(h => h.FileName));
        Assert.Equal("Team", scoped[0].ShareName);
        Assert.Equal(new[] { "c.txt" }, underscore.Select(h => h.FileName));
    }

    [PostgresFact]
    public async Task Search_LeavesRecycleBinOut_UnlessRequested()
    {
        await Put("docs/a.txt", "budget");
        await Put(".RECYCLE_BIN/b.txt", "budget");
        await Put("uid/.recycle_bin/c.txt", "budget");
        await Put("docs/RECYCLE_BIN_notes/d.txt", "budget");

        var plain = await Find("budget");
        var all = await Repo.SearchAsync(new[] { "budget" }, null, null, 0, 50, includeRecycleBin: true);

        Assert.Equal(new[] { "a.txt", "d.txt" }, plain.Select(h => h.FileName).Order());
        Assert.Equal(4, all.Count);
    }

    [PostgresFact]
    public async Task Upsert_KeepsCreated_AndTouchIfUnchangedDetectsStaleStamps()
    {
        await Put("a.txt", "old");
        var updated = Doc("a.txt", "new");
        updated.Created = T0.AddDays(1);
        await Repo.UpsertAsync(updated, T0.AddHours(1), T0.AddHours(1));

        var hit = Assert.Single(await Find("new"));
        Assert.Equal(T0, hit.Created);
        Assert.True(await Repo.TouchIfUnchangedAsync(updated.Id, 0, T0.AddHours(1), T0.AddHours(2)));
        Assert.False(await Repo.TouchIfUnchangedAsync(updated.Id, 0, T0.AddHours(3), T0.AddHours(3)));
        Assert.False(await Repo.TouchIfUnchangedAsync(Md5("missing"), 0, T0, T0));
    }

    [PostgresFact]
    public async Task MoveDirectory_RewritesIdsPathsAndNames_AndReplacesTargetEntries()
    {
        await Put("old", dir: true);
        await Put("old/sub/a.txt", "alpha");
        await Put("new/stale.txt", "alpha");
        await Put("oldish/keep.txt", "alpha");

        int moved = await Repo.MoveAsync(Abs("old"), Abs("new"), "Team", "new", T0.AddHours(1));

        Assert.Equal(2, moved);
        var hits = await Find("alpha");
        var a = Assert.Single(hits, h => h.FileName == "a.txt");
        Assert.Equal(Abs("new/sub/a.txt"), a.AbsolutePath);
        Assert.Equal("new" + Sep + "sub" + Sep + "a.txt", a.SharePath);
        Assert.Equal(Md5(Abs("new/sub/a.txt")), a.Id);
        Assert.DoesNotContain(hits, h => h.FileName == "stale.txt");
        Assert.Contains(hits, h => h.FileName == "keep.txt");
        Assert.Equal(Md5(Abs("new")), Assert.Single(await Find("new"), h => h.IsDirectory).Id);
    }

    [PostgresFact]
    public async Task MoveFile_UpdatesNameAndType()
    {
        await Put("a.txt", "alpha");

        await Repo.MoveAsync(Abs("a.txt"), Abs("b.md"), "Team", "b.md", T0.AddHours(1));

        var hit = Assert.Single(await Find("alpha"));
        Assert.Equal("b.md", hit.FileName);
        Assert.Equal("md", hit.FileType);
    }

    [PostgresFact]
    public async Task MoveDirectory_WithNonBmpName_KeepsChildPathsIntact_AndSurvivesSweep()
    {
        // "😀" is two UTF-16 units but one PostgreSQL character: the remainder must be cut by
        // code points, or the child loses its separator ("new" + "a.txt").
        await Put("old😀", dir: true);
        await Put("old😀/a.txt", "alpha");

        await Repo.MoveAsync(Abs("old😀"), Abs("new"), "Team", "new", T0.AddHours(1));

        // A reindex pass that started before the move (at T0 + 30 min) walked the old paths;
        // the moved rows must not be swept as untouched.
        Assert.Equal(0, await Repo.SweepAsync(new[] { "Team" }, T0.AddMinutes(30)));
        var a = Assert.Single(await Find("alpha"));
        Assert.Equal(Abs("new/a.txt"), a.AbsolutePath);
        Assert.Equal("new" + Sep + "a.txt", a.SharePath);
        Assert.Equal(Md5(Abs("new/a.txt")), a.Id);
    }

    [PostgresFact]
    public async Task DeleteTree_RemovesFolderAndDescendantsButNotSiblings()
    {
        await Put("old", dir: true);
        await Put("old/a.txt", "alpha");
        await Put("old2/b.txt", "alpha");

        Assert.Equal(2, await Repo.DeleteTreeAsync(Abs("old")));
        Assert.Equal(new[] { "b.txt" }, (await Find("alpha")).Select(h => h.FileName));
    }

    [PostgresFact]
    public async Task Sweep_RemovesUntouchedDocumentsOfTheWalkedSharesOnly()
    {
        await Put("gone.txt", "alpha");
        await Put("kept.txt", "alpha");
        await Put("elsewhere.txt", "alpha", share: "Other");
        await Repo.TouchAsync(Md5(Abs("kept.txt")), T0.AddHours(1));

        Assert.Equal(1, await Repo.SweepAsync(new[] { "Team" }, T0.AddMinutes(30)));
        Assert.Equal(new[] { "elsewhere.txt", "kept.txt" },
            (await Find("alpha")).Select(h => h.FileName).Order());
        Assert.Equal(2, (await Repo.GetStatsAsync()).Documents);
    }
}

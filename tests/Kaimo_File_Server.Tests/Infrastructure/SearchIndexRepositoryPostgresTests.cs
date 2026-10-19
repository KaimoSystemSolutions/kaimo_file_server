using System.Security.Cryptography;
using System.Text;
using Kaimo_File_Server.Infrastructure.Repositories;
using Kaimo_File_Server.Search;
using Kaimo_File_Server.Tests.Infrastructure;
using Npgsql;
using Xunit;

namespace Kaimo_File_Server.Tests;

/// <summary>
/// The local search index against real PostgreSQL (pg_trgm, tsvector, md5(), ILIKE, LIKE escaping).
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
        var excerpts = await Repo.GetExcerptsAsync(hits.Select(h => h.Id).ToList(), ["report"]);
        Assert.Contains("report", excerpts[hits[1].Id]);
        Assert.False(excerpts.ContainsKey(hits[0].Id)); // name hit only, no content match
        Assert.Equal(new[] { "notes.txt" }, (await Find("arterl")).Select(h => h.FileName));
    }

    [PostgresFact]
    public async Task Search_RanksAnyNameMatchAboveContentOnlyMatches_AndMoreNameTermsFirst()
    {
        // Content matching every term (3 x 3 points) used to outrank a name matching one (4).
        await Put("notes.txt", "quarterly budget report, budget report, quarterly");
        await Put("budget.txt", "nothing relevant");
        await Put("budget-report.txt", "nothing relevant");

        Assert.Equal(new[] { "budget-report.txt", "budget.txt", "notes.txt" },
            (await Find("quarterly", "budget", "report")).Select(h => h.FileName));
    }

    [PostgresFact]
    public async Task Search_RanksFoldersAboveFiles_EvenWithFewerNameTerms()
    {
        await Put("budget-report.txt", "budget report");
        await Put("archive/budget", dir: true);
        await Put("notes.txt", "budget report");

        Assert.Equal(new[] { "budget", "budget-report.txt", "notes.txt" },
            (await Find("budget", "report")).Select(h => h.FileName));
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
    public async Task ResetContentStamps_ClearsEveryRowAcrossBatchBoundaries()
    {
        var names = Enumerable.Range(0, 7).Select(i => $"f{i}.txt").ToList();
        foreach (var name in names)
            await Put(name, "x");

        // Batch size 3 forces three full/partial batches plus the empty terminating one.
        await Repo.ResetContentStampsAsync(3, CancellationToken.None);

        foreach (var name in names)
            Assert.False(await Repo.TouchIfUnchangedAsync(Doc(name).Id, 0, T0, T0.AddHours(1)));
        // A second run over already cleared rows is a harmless no-op.
        await Repo.ResetContentStampsAsync(3, CancellationToken.None);
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

    private Task<List<FileDocument>> Query(string text)
        => Repo.SearchAsync(LocalIndexSearchService.Tokenize(text), null, null, 0, 50);

    [PostgresFact]
    public async Task Search_MatchesDecomposedNamesAndSharpS()
    {
        await Put("Kündigung.txt");                 // macOS client: NFD name
        await Put("address.txt", "Hauptstraße 5");
        await Put("old.txt", "HAUPTSTRASSE 7");

        Assert.Equal(["Kündigung.txt"], (await Query("Kündigung")).Select(h => h.FileName));
        Assert.Equal(["address.txt", "old.txt"], (await Query("hauptstrasse")).Select(h => h.FileName).Order());
        Assert.Equal(["address.txt", "old.txt"], (await Query("Hauptstraße")).Select(h => h.FileName).Order());
    }

    [PostgresFact]
    public async Task Search_ExcerptStartsAtTheFirstMatchOfAnyTerm_CaseInsensitively()
    {
        await Put("long.txt", new string('x', 5000) + " Delta and GAMMA " + new string('y', 5000));

        var hit = Assert.Single(await Find("gamma", "delta"));
        var excerpts = await Repo.GetExcerptsAsync([hit.Id], ["gamma", "delta"]);

        Assert.Contains("Delta and GAMMA", excerpts[hit.Id]);
    }

    [PostgresFact]
    public async Task Search_TwoCharTermBesideALongerOne_OnlyRanks()
    {
        await Put("kw42-only.txt", "nothing");
        await Put("bericht.txt", "nothing");
        await Put("kw42-bericht.txt", "nothing");
        await Put("notes.txt", "der bericht zur kw");

        Assert.Equal(new[] { "kw42-bericht.txt", "bericht.txt", "notes.txt" },
            (await Find("kw", "42", "bericht")).Select(h => h.FileName));
    }

    [PostgresFact]
    public async Task Search_TwoCharTermBesideALongerOne_StaysOnTheIndexes()
    {
        await Put("a.txt", "bericht");
        var (sql, parameters) = SearchIndexRepository.BuildSearchSql(["kw", "42", "bericht"], null, null, 0, 50);

        await using var connection = new NpgsqlConnection(ConnectionString);
        await connection.OpenAsync();
        await using (var off = new NpgsqlCommand("SET enable_seqscan = off", connection))
            await off.ExecuteNonQueryAsync();
        await using var explain = new NpgsqlCommand("EXPLAIN " + sql, connection);
        foreach (var p in parameters.Cast<NpgsqlParameter>())
            explain.Parameters.Add(p.Clone());
        var plan = new StringBuilder();
        await using (var reader = await explain.ExecuteReaderAsync())
            while (await reader.ReadAsync())
                plan.AppendLine(reader.GetString(0));

        // With seq scans disabled the planner still falls back to one when no index path exists.
        Assert.DoesNotContain("Seq Scan on search_documents", plan.ToString());
    }

    [PostgresFact]
    public async Task Search_UsesTheIndexes_EvenWhereThePlannerWouldScan()
    {
        // A small table makes a sequential scan look cheapest, but it reads every document's
        // word vector (measured: 343 ms on a 3k-document dev index vs. a few ms).
        for (int i = 0; i < 50; i++)
            await Put($"doc{i}.txt", "some ordinary text " + i);
        var plans = new PlanRecorder();

        var hits = await new SearchIndexRepository(FactoryWith(plans)).SearchAsync(["ordinary"], null, null, 0, 50);

        Assert.Equal(50, hits.Count);
        var plan = Assert.Single(plans.Plans);
        Assert.DoesNotContain("Seq Scan on search_documents", plan);
        Assert.DoesNotContain("Seq Scan on search_terms", plan);
    }

    /// <summary>EXPLAINs each query on its own connection and transaction, so session settings apply.</summary>
    private sealed class PlanRecorder : Microsoft.EntityFrameworkCore.Diagnostics.DbCommandInterceptor
    {
        public List<string> Plans { get; } = new();

        public override async ValueTask<Microsoft.EntityFrameworkCore.Diagnostics.InterceptionResult<System.Data.Common.DbDataReader>> ReaderExecutingAsync(
            System.Data.Common.DbCommand command, Microsoft.EntityFrameworkCore.Diagnostics.CommandEventData eventData,
            Microsoft.EntityFrameworkCore.Diagnostics.InterceptionResult<System.Data.Common.DbDataReader> result,
            CancellationToken ct = default)
        {
            if (command.CommandText.Contains("WITH pts", StringComparison.Ordinal))
            {
                await using var explain = new NpgsqlCommand("EXPLAIN " + command.CommandText,
                    (NpgsqlConnection)command.Connection!, (NpgsqlTransaction?)command.Transaction);
                foreach (NpgsqlParameter p in command.Parameters)
                    explain.Parameters.Add(p.Clone());
                var plan = new StringBuilder();
                await using (var reader = await explain.ExecuteReaderAsync(ct))
                    while (await reader.ReadAsync(ct))
                        plan.AppendLine(reader.GetString(0));
                Plans.Add(plan.ToString());
            }
            return result;
        }
    }

    [PostgresFact]
    public async Task GetExcerpts_FindsAMatchBeyondTheLeadingWindow()
    {
        // The excerpt is looked for in the leading characters first (cheap); a match only
        // further in must still be found.
        await Put("deep.txt", string.Concat(Enumerable.Repeat("filler ", 6000)) + "the Budget line");

        var excerpts = await Repo.GetExcerptsAsync([Md5(Abs("deep.txt"))], ["budget"]);

        Assert.Contains("the Budget line", excerpts[Md5(Abs("deep.txt"))]);
    }

    [PostgresFact]
    public async Task GetExcerpts_SkipsUnknownIdsAndTwoCharTerms()
    {
        await Put("a.txt", "ab cd text");

        Assert.Empty(await Repo.GetExcerptsAsync([Md5(Abs("a.txt"))], ["ab"]));
        Assert.Empty(await Repo.GetExcerptsAsync(["missing"], ["text"]));
        Assert.Empty(await Repo.GetExcerptsAsync([], ["text"]));
    }

    [PostgresFact]
    public async Task Search_FindsWordParts_InsideMailAddressesAndUrls()
    {
        // The word parser keeps an address or URL as one word; the term dictionary still
        // finds the parts inside it, like the Elasticsearch n-grams do.
        await Put("contact.txt", "Kontakt: max@firma.de, siehe https://www.kaimo.de/docs/handbuch");

        Assert.Equal(["contact.txt"], (await Find("firma")).Select(h => h.FileName));
        Assert.Equal(["contact.txt"], (await Find("handbuch")).Select(h => h.FileName));
        Assert.Equal(["contact.txt"], (await Find("ntak")).Select(h => h.FileName));
        Assert.Empty(await Find("firmade")); // never across word boundaries
    }

    [PostgresFact]
    public async Task Search_RanksWholeWordsAboveWordParts_AnywhereInTheContent()
    {
        // The whole word sits far beyond the first 20,000 characters the regex ranking used to read.
        await Put("deep.txt", string.Concat(Enumerable.Repeat("filler ", 5000)) + "budget");
        await Put("early.txt", "budgeting starts here");

        Assert.Equal(["deep.txt", "early.txt"], (await Find("budget")).Select(h => h.FileName));
    }

    [PostgresFact]
    public async Task Upsert_ContentBeyondTheWordVectorLimit_StillIndexesItsLeadingWords()
    {
        // 250,000 distinct three-character CJK words: 2.25 MB of lexemes, more than a tsvector holds.
        static string Word(int i) => new([(char)(0x4E00 + i % 20000), (char)(0x4E00 + i / 20000), '一']);
        var content = string.Join(' ', Enumerable.Range(0, 250_000).Select(Word));

        await Put("huge.txt", content);

        Assert.Equal(["huge.txt"], (await Find(Word(1))).Select(h => h.FileName));
    }

    [PostgresFact]
    public async Task Compact_DropsDictionaryWordsNoDocumentUsesAnyMore()
    {
        await Put("a.txt", "zebra");
        await Put("b.txt", "yak");
        await Repo.DeleteAsync(Md5(Abs("a.txt")));

        Assert.Equal(["zebra", "yak"], await Terms("zebra", "yak"));
        await Repo.CompactAsync();

        Assert.Equal(["yak"], await Terms("zebra", "yak"));
        Assert.Equal(["b.txt"], (await Find("yak")).Select(h => h.FileName));
    }

    private async Task<List<string>> Terms(params string[] words)
    {
        await using var connection = new NpgsqlConnection(ConnectionString);
        await connection.OpenAsync();
        await using var cmd = new NpgsqlCommand(
            "SELECT \"Term\" FROM search_terms WHERE \"Term\" = ANY(@w)", connection);
        cmd.Parameters.AddWithValue("w", words);
        var found = new List<string>();
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
            found.Add(reader.GetString(0));
        return words.Where(found.Contains).ToList();
    }
}

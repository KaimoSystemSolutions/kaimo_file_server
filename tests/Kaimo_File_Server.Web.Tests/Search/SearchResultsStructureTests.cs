using Kaimo_File_Server.Search;
using Kaimo_File_Server.Web.Components.Search;
using Xunit;

namespace Kaimo_File_Server.Web.Tests.Search;

/// <summary>
/// Structure of search results: per-share groups in the dropdown (capped, collapsible),
/// the file-tree view of the results page, and the links between the two.
/// </summary>
public sealed class SearchResultsStructureTests
{
    private static FileDocument Hit(string share, string path, bool dir = false) => new()
    {
        Id = $"{share}:{path}",
        FileName = path.Replace('\\', '/').Split('/')[^1],
        ShareName = share,
        AbsolutePath = "/" + path,
        SharePath = path,
        FileType = "",
        IsDirectory = dir,
    };

    // ── Dropdown groups ──

    [Fact]
    public void Groups_KeepRankOrderOfSharesAndCapEachShare()
    {
        var hits = new[]
        {
            Hit("b", "1.txt"), Hit("a", "1.txt"), Hit("b", "2.txt"), Hit("b", "3.txt"), Hit("a", "2.txt"),
        };

        var groups = SearchHitGroups.Build(hits, perShare: 2, collapsed: new HashSet<string>());

        Assert.Equal(["b", "a"], groups.Select(g => g.Share));
        Assert.Equal(3, groups[0].Total);
        Assert.Equal(["1.txt", "2.txt"], groups[0].Shown.Select(h => h.FileName));
        Assert.Equal(2, groups[1].Total);
        Assert.Equal(2, groups[1].Shown.Count);
    }

    [Fact]
    public void Groups_CollapsedShareKeepsItsCountButShowsNothing()
    {
        var hits = new[] { Hit("a", "1.txt"), Hit("a", "2.txt"), Hit("b", "1.txt") };

        var groups = SearchHitGroups.Build(hits, perShare: 5, collapsed: new HashSet<string> { "a" });

        Assert.True(groups[0].Collapsed);
        Assert.Equal(2, groups[0].Total);
        Assert.Empty(groups[0].Shown);
        Assert.False(groups[1].Collapsed);
        Assert.Single(groups[1].Shown);
    }

    [Fact]
    public void Groups_Empty_ReturnsNoGroups()
        => Assert.Empty(SearchHitGroups.Build([], 5, new HashSet<string>()));

    // ── Results tree ──

    [Fact]
    public void Tree_BuildsShareFolderFileHierarchy_WithIntermediateFolders()
    {
        var roots = SearchResultTree.Build([Hit("docs", "a/b/report.txt"), Hit("docs", "a/notes.txt")]);

        var share = Assert.Single(roots);
        Assert.Equal("docs", share.Name);
        Assert.True(share.IsShare);
        Assert.Null(share.Hit);
        Assert.Equal(2, share.HitCount);

        var a = Assert.Single(share.Children);
        Assert.Equal("a", a.Name);
        Assert.Equal("a", a.SharePath);
        Assert.True(a.IsDirectory);
        Assert.Null(a.Hit);   // only on the path, not a hit itself

        // Folders first, then files.
        Assert.Equal(["b", "notes.txt"], a.Children.Select(c => c.Name));
        var report = Assert.Single(a.Children[0].Children);
        Assert.Equal("a/b/report.txt", report.SharePath);
        Assert.NotNull(report.Hit);
        Assert.False(report.IsDirectory);
    }

    [Fact]
    public void Tree_FolderHitWithHitsBelow_IsOneNodeCarryingTheHit()
    {
        var roots = SearchResultTree.Build([Hit("s", "a/x.txt"), Hit("s", "a", dir: true)]);

        var a = Assert.Single(roots[0].Children);
        Assert.NotNull(a.Hit);
        Assert.True(a.IsDirectory);
        Assert.Single(a.Children);
        Assert.Equal(2, roots[0].HitCount);
        Assert.Equal(2, a.HitCount);
    }

    [Fact]
    public void Tree_NormalizesBackslashesAndLeadingSlashes_AndSortsShares()
    {
        var roots = SearchResultTree.Build([Hit("zeta", "\\dir\\f.txt"), Hit("Alpha", "/f.txt")]);

        Assert.Equal(["Alpha", "zeta"], roots.Select(r => r.Name));
        Assert.Equal("dir", roots[1].Children[0].SharePath);
        Assert.Equal("dir/f.txt", roots[1].Children[0].Children[0].SharePath);
    }

    [Fact]
    public void Flatten_GivesDepthAndHidesChildrenOfCollapsedNodes()
    {
        var roots = SearchResultTree.Build([Hit("s", "a/b.txt"), Hit("s", "c.txt")]);

        var open = SearchResultTree.Flatten(roots, new HashSet<string>()).ToList();
        Assert.Equal(["s", "a", "b.txt", "c.txt"], open.Select(r => r.Node.Name));
        Assert.Equal([0, 1, 2, 1], open.Select(r => r.Depth));

        var folded = SearchResultTree.Flatten(roots, new HashSet<string> { roots[0].Children[0].Key }).ToList();
        Assert.Equal(["s", "a", "c.txt"], folded.Select(r => r.Node.Name));
    }

    [Fact]
    public void Tree_SameFolderNameInTwoShares_StaysSeparate()
    {
        var roots = SearchResultTree.Build([Hit("s1", "a/x.txt"), Hit("s2", "a/y.txt")]);

        Assert.NotEqual(roots[0].Children[0].Key, roots[1].Children[0].Key);
    }

    // ── Navigation into the file browser ──

    [Theory]
    [InlineData("docs/a/report.txt", false, "/files/s/docs/a")]
    [InlineData("report.txt", false, "/files/s")]
    [InlineData("docs\\a", true, "/files/s/docs/a")]
    [InlineData("", true, "/files/s")]
    public void BrowserUrl_OpensFolderOrTheFilesFolder(string path, bool dir, string expected)
        => Assert.Equal(expected, SearchHitNavigation.BrowserUrl("/files/s", path, dir));

    [Fact]
    public void ToWebHit_MapsOwnHomeHitToUserRoute_AndLeavesOthers()
    {
        var me = Guid.NewGuid();
        var own = SearchHitNavigation.ToWebHit(Hit("users", $"{me}/docs/a.txt"), me);
        var other = SearchHitNavigation.ToWebHit(Hit("users", $"{Guid.NewGuid()}/a.txt"), me);
        var plain = SearchHitNavigation.ToWebHit(Hit("data", "a.txt"), me);

        Assert.Equal(("user", "docs/a.txt"), (own.ShareName, own.SharePath));
        Assert.Equal("users", other.ShareName);
        Assert.Equal("data", plain.ShareName);
    }

    // ── Request handed to the results page (browser history state, not the URL) ──

    [Fact]
    public void Request_RoundTripsThroughHistoryState()
    {
        var me = Guid.NewGuid();
        var request = SearchResultsRequest.Create("a&b ü", [("docs", "a/b"), ("x", "")], true, me,
            focus: ("docs", "a/b"));

        var parsed = SearchResultsRequest.Parse(request.Serialize());

        Assert.NotNull(parsed);
        Assert.Equal("a&b ü", parsed!.Query);
        Assert.True(parsed.IncludeRecycleBin);
        Assert.Equal([new SearchResultsTarget("docs", "a/b"), new SearchResultsTarget("x", "")], parsed.Targets);
        Assert.Equal(new SearchResultsTarget("docs", "a/b"), parsed.Focus);
    }

    [Fact]
    public void Request_GlobalScope_HasNoShareTargets()
    {
        var request = SearchResultsRequest.Create("x", [(null, "")], false, Guid.NewGuid());

        Assert.True(request.IsGlobal);
        Assert.Equal([((string?)null, "")], request.ToSearchTargets(Guid.NewGuid()));
    }

    [Fact]
    public void Request_OwnHome_NeverCarriesTheHomeGuid_ButSearchesTheRealFolder()
    {
        var me = Guid.NewGuid();

        var request = SearchResultsRequest.Create("x", [("users", $"{me}/docs")], false, me);

        Assert.Equal([new SearchResultsTarget("user", "docs")], request.Targets);
        Assert.DoesNotContain(me.ToString(), request.Serialize());
        Assert.Equal([((string?)"users", $"{me}/docs")], request.ToSearchTargets(me));
        Assert.Equal([((string?)"users", me.ToString())],
            SearchResultsRequest.Create("x", [("users", me.ToString())], false, me).ToSearchTargets(me));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("{\"Query\":null}")]
    [InlineData("{\"Query\":\"x\",\"Targets\":null}")]
    public void Request_Parse_RejectsMissingOrBrokenState(string? state)
        => Assert.Null(SearchResultsRequest.Parse(state));

    [Fact]
    public void Request_Parse_CapsOversizedState()
    {
        var targets = Enumerable.Range(0, 500).Select(i => ((string?)$"s{i}", ""));
        var request = SearchResultsRequest.Create(new string('q', 5000), targets, false, Guid.NewGuid());

        var parsed = SearchResultsRequest.Parse(request.Serialize())!;

        Assert.Equal(SearchResultsRequest.MaxQueryLength, parsed.Query.Length);
        Assert.Equal(SearchResultsRequest.MaxTargets, parsed.Targets.Count);
    }

    [Fact]
    public void Request_Describe_NamesTheScope()
    {
        var me = Guid.NewGuid();
        Assert.Equal("All", SearchResultsRequest.Create("x", [(null, "")], false, me).Describe("All"));
        Assert.Equal("docs/a, b", SearchResultsRequest.Create("x", [("docs", "a"), ("b", "")], false, me).Describe("All"));
    }

    // ── Sorting and focus ──

    [Fact]
    public void Groups_ShowTheBestHitsSortedFoldersFirstThenByName()
    {
        var hits = new[]
        {
            Hit("s", "zeta.txt"), Hit("s", "b", dir: true), Hit("s", "Äpfel.txt"), Hit("s", "a", dir: true), Hit("s", "dropped.txt"),
        };

        var group = Assert.Single(SearchHitGroups.Build(hits, perShare: 4, collapsed: new HashSet<string>()));

        // The 5th-ranked hit is cut, the rest is shown folders first, alphabetically (Ä next to A).
        Assert.Equal(["a", "b", "Äpfel.txt", "zeta.txt"], group.Shown.Select(h => h.FileName));
        Assert.Equal(5, group.Total);
    }

    [Fact]
    public void Tree_SortsAlphabeticallyLikeTheFileBrowser()
    {
        var roots = SearchResultTree.Build([Hit("s", "Zebra.txt"), Hit("s", "Äpfel.txt"), Hit("s", "_x", dir: true), Hit("s", "b.txt")]);

        Assert.Equal(["_x", "Äpfel.txt", "b.txt", "Zebra.txt"], roots[0].Children.Select(c => c.Name));
    }

    [Fact]
    public void KeyOf_MatchesTheNodeKeys()
    {
        var roots = SearchResultTree.Build([Hit("s", "a/b/x.txt")]);

        Assert.Equal(roots[0].Key, SearchResultTree.KeyOf("s", ""));
        Assert.Equal(roots[0].Children[0].Children[0].Key, SearchResultTree.KeyOf("s", "/a/b/"));
    }

    // ── Filter on the results page ──

    [Theory]
    [InlineData("REPORT", true)]
    [InlineData("docs/sub", true)]
    [InlineData("nothing", false)]
    [InlineData("", true)]
    public void Filter_TextMatchesNameOrPath(string text, bool expected)
        => Assert.Equal(expected, new SearchResultsFilter(text, SearchTypeFilter.All, SearchDateFilter.Any)
            .Matches(Hit("s", "docs/sub/report.pdf"), DateTime.UtcNow));

    [Theory]
    [InlineData("a.pdf", false, SearchTypeFilter.Documents, true)]
    [InlineData("a.docx", false, SearchTypeFilter.Documents, true)]
    [InlineData("a.png", false, SearchTypeFilter.Documents, false)]
    [InlineData("a.png", false, SearchTypeFilter.Images, true)]
    [InlineData("a.mp4", false, SearchTypeFilter.Media, true)]
    [InlineData("a.mp3", false, SearchTypeFilter.Media, true)]
    [InlineData("a.zip", false, SearchTypeFilter.Archives, true)]
    [InlineData("a", true, SearchTypeFilter.Folders, true)]
    [InlineData("a.txt", false, SearchTypeFilter.Folders, false)]
    [InlineData("a", true, SearchTypeFilter.Files, false)]
    [InlineData("a.txt", false, SearchTypeFilter.Files, true)]
    public void Filter_TypeCategories(string name, bool dir, SearchTypeFilter type, bool expected)
        => Assert.Equal(expected, new SearchResultsFilter("", type, SearchDateFilter.Any)
            .Matches(Hit("s", name, dir), DateTime.UtcNow));

    [Fact]
    public void Filter_ModifiedWithinPeriod()
    {
        var now = new DateTime(2026, 11, 21, 12, 0, 0, DateTimeKind.Utc);
        var hit = Hit("s", "a.txt");
        hit.Modified = now.AddDays(-3);
        var week = new SearchResultsFilter("", SearchTypeFilter.All, SearchDateFilter.Week);
        var day = new SearchResultsFilter("", SearchTypeFilter.All, SearchDateFilter.Day);

        Assert.True(week.Matches(hit, now));
        Assert.False(day.Matches(hit, now));
        Assert.False(new SearchResultsFilter("", SearchTypeFilter.All, SearchDateFilter.Any).IsActive);
        Assert.True(week.IsActive);
    }

    [Fact]
    public void Filter_Day_IsTheLast24Hours_NotTheUtcCalendarDay()
    {
        // 01:00 UTC: a file from 23:00 UTC yesterday is two hours old.
        var now = new DateTime(2026, 11, 21, 1, 0, 0, DateTimeKind.Utc);
        var hit = Hit("s", "a.txt");
        var day = new SearchResultsFilter("", SearchTypeFilter.All, SearchDateFilter.Day);

        hit.Modified = now.AddHours(-2);
        Assert.True(day.Matches(hit, now));
        hit.Modified = now.AddHours(-25);
        Assert.False(day.Matches(hit, now));
    }

    [Fact]
    public void Filter_Month_IsTheLast30Days()
    {
        // March 31: one calendar month back would reach February 28.
        var now = new DateTime(2026, 3, 31, 0, 0, 0, DateTimeKind.Utc);
        var hit = Hit("s", "a.txt");
        var month = new SearchResultsFilter("", SearchTypeFilter.All, SearchDateFilter.Month);

        hit.Modified = new DateTime(2026, 2, 28, 12, 0, 0, DateTimeKind.Utc);
        Assert.False(month.Matches(hit, now));
        hit.Modified = new DateTime(2026, 3, 2, 0, 0, 0, DateTimeKind.Utc);
        Assert.True(month.Matches(hit, now));
    }

    // ── Capped counts and "n more" focus ──

    [Theory]
    [InlineData(3, false, "3")]
    [InlineData(50, true, "50+")]
    public void CountLabel_MarksCappedCounts(int count, bool capped, string expected)
        => Assert.Equal(expected, SearchHitGroups.CountLabel(count, capped));

    [Fact]
    public void FocusFor_MatchesTheSearchedFolderIgnoringShareCase()
    {
        var user = Guid.NewGuid();

        // The scope came from the URL (/files/Docs/...), the hits carry the stored name "docs".
        Assert.Equal(new SearchResultsTarget("docs", "reports/2024"),
            SearchResultsRequest.FocusFor("docs", [("Docs", "reports/2024")], user));
        Assert.Equal(new SearchResultsTarget("docs", ""),
            SearchResultsRequest.FocusFor("docs", [(null, "")], user));
        Assert.Equal(new SearchResultsTarget("other", ""),
            SearchResultsRequest.FocusFor("other", [("Docs", "reports")], user));
    }

    [Fact]
    public void Tree_Find_IgnoresCaseOnlyWhenAsked()
    {
        var roots = SearchResultTree.Build([Hit("docs", "Reports/a.txt")]);

        Assert.Null(SearchResultTree.Find(roots, "docs/reports"));
        Assert.Equal("docs/Reports", SearchResultTree.Find(roots, "docs/reports", StringComparison.OrdinalIgnoreCase)?.Key);
        Assert.Equal("docs/Reports/a.txt", SearchResultTree.Find(roots, "docs/Reports/a.txt")?.Key);
    }

    [Fact]
    public void Filter_CustomRange_IncludesBothDays()
    {
        var hit = Hit("s", "a.txt");
        var range = new SearchResultsFilter("", SearchTypeFilter.All, SearchDateFilter.Custom,
            From: new DateOnly(2026, 11, 1), To: new DateOnly(2026, 11, 10));

        hit.Modified = new DateTime(2026, 11, 1, 0, 0, 0, DateTimeKind.Utc);
        Assert.True(range.Matches(hit, DateTime.UtcNow));
        hit.Modified = new DateTime(2026, 11, 10, 23, 59, 0, DateTimeKind.Utc);
        Assert.True(range.Matches(hit, DateTime.UtcNow));
        hit.Modified = new DateTime(2026, 11, 11, 0, 0, 1, DateTimeKind.Utc);
        Assert.False(range.Matches(hit, DateTime.UtcNow));
        hit.Modified = new DateTime(2026, 10, 31, 23, 59, 0, DateTimeKind.Utc);
        Assert.False(range.Matches(hit, DateTime.UtcNow));
    }

    [Fact]
    public void Filter_CustomRange_OpenEndsAndMissingBoundsAreInactive()
    {
        var hit = Hit("s", "a.txt");
        hit.Modified = new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        Assert.True(new SearchResultsFilter("", SearchTypeFilter.All, SearchDateFilter.Custom, To: new DateOnly(2020, 1, 1))
            .Matches(hit, DateTime.UtcNow));
        Assert.False(new SearchResultsFilter("", SearchTypeFilter.All, SearchDateFilter.Custom, From: new DateOnly(2021, 1, 1))
            .Matches(hit, DateTime.UtcNow));
        var empty = new SearchResultsFilter("", SearchTypeFilter.All, SearchDateFilter.Custom);
        Assert.True(empty.Matches(hit, DateTime.UtcNow));
        Assert.False(empty.IsActive);
    }

    [Fact]
    public void Filter_ContentHits_KeepOnlyHitsFoundInTheTextSearch()
    {
        var a = Hit("s", "a.txt");
        var b = Hit("s", "b.txt");
        var filter = new SearchResultsFilter("", SearchTypeFilter.All, SearchDateFilter.Any,
            ContentHits: new HashSet<string> { a.Id });

        Assert.True(filter.Matches(a, DateTime.UtcNow));
        Assert.False(filter.Matches(b, DateTime.UtcNow));
        Assert.True(filter.IsActive);
    }

    [Theory]
    [InlineData("<mark>report</mark>-januar.pdf", "report-januar.pdf", false)]
    [InlineData("<mark>Report</mark>-Januar.PDF", "report-januar.pdf", false)]
    [InlineData("Umsatz im <mark>Report</mark> Januar", "report-januar.pdf", true)]
    [InlineData(null, "a.txt", false)]
    [InlineData("", "a.txt", false)]
    public void Snippet_IsShownOnlyWhenItAddsToTheName(string? snippet, string name, bool expected)
        => Assert.Equal(expected, SearchSnippet.AddsTo(snippet, name));
}

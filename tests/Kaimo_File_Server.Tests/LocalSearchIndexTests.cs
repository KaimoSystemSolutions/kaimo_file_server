using System.Text;
using Kaimo_File_Server.Infrastructure.Repositories;
using Kaimo_File_Server.Search;
using Npgsql;
using Xunit;

namespace Kaimo_File_Server.Tests;

/// <summary>
/// Database-free checks of the local (PostgreSQL) search engine: query tokenizing, highlight
/// marking, content preparation, the SQL builder's parameter binding and the engine-setting
/// migration from the legacy Elasticsearch flag.
/// </summary>
public sealed class LocalSearchIndexTests
{
    [Fact]
    public void Tokenize_SplitsOnNonLetters_LowercasesDedupesAndDropsSingleChars()
    {
        var tokens = LocalIndexSearchService.Tokenize("Report_2024.PDF a report Ünter");

        Assert.Equal(new[] { "report", "2024", "pdf", "ünter" }, tokens);
    }

    [Fact]
    public void Tokenize_StripsLikeAndRegexMetacharacters()
    {
        var tokens = LocalIndexSearchService.Tokenize(@"%_\ .* ' OR 1=1 --");

        Assert.Equal(new[] { "or" }, tokens);
    }

    [Fact]
    public void Tokenize_CapsTheNumberOfTerms()
    {
        var text = string.Join(" ", Enumerable.Range(0, 50).Select(i => "term" + i));

        Assert.Equal(LocalIndexSearchService.MaxQueryTokens, LocalIndexSearchService.Tokenize(text).Count);
    }

    [Fact]
    public void Mark_WrapsEveryCaseInsensitiveOccurrence_AndMergesOverlaps()
    {
        var marked = LocalIndexSearchService.Mark("Document docs", new[] { "doc", "ocum" });

        Assert.Equal("<mark>Docum</mark>ent <mark>doc</mark>s", marked);
    }

    [Fact]
    public void BuildHighlight_PrefersFileName_ThenContentExcerpt()
    {
        string[] tokens = ["invoice"];

        Assert.Equal("<mark>Invoice</mark>.pdf",
            LocalIndexSearchService.BuildHighlight("Invoice.pdf", "the invoice text", tokens));
        Assert.Equal("the <mark>invoice</mark> text",
            LocalIndexSearchService.BuildHighlight("scan.pdf", "the invoice text", tokens));
        Assert.Null(LocalIndexSearchService.BuildHighlight("scan.pdf", null, tokens));
    }

    [Fact]
    public void PrepareContent_RemovesNulAndCapsLength()
    {
        Assert.Equal("ab", LocalIndexSearchService.PrepareContent("a\0b"));
        Assert.Equal(LocalIndexSearchService.MaxContentChars,
            LocalIndexSearchService.PrepareContent(new string('x', LocalIndexSearchService.MaxContentChars + 5)).Length);
    }

    [Fact]
    public void BuildSearchSql_BindsAllInput_AndSkipsContentForTwoCharTerms()
    {
        var (sql, parameters) = SearchIndexRepository.BuildSearchSql(
            new[] { "ab", "report" }, "Team's", "a%b/c_d", offset: 50, limit: 50);

        var values = parameters.Cast<NpgsqlParameter>().Select(p => p.Value).ToList();
        // No user value is spliced into the statement text.
        Assert.DoesNotContain("Team's", sql);
        Assert.DoesNotContain("report", sql);
        Assert.Contains("Team's", values);
        // LIKE wildcards in the folder prefix are escaped, so they match literally.
        Assert.Contains(@"a\%b/c\_d/%", values);
        // A two-character term only checks the file name (no trigram index for content).
        Assert.Equal(1, values.Count(v => Equals(v, "%ab%")));
        Assert.DoesNotContain(@"\mab\M", values);
        Assert.Contains(@"\mreport\M", values);
    }

    [Fact]
    public void StripEncodedBlobs_DropsBase64AndHexPayloads_KeepsWordsAndUrls()
    {
        var blob = Convert.ToBase64String(Enumerable.Range(0, 300).Select(i => (byte)(i * 37)).ToArray());
        var hash = new string('a', 30) + "0123456789abcdef0123456789abcdef0123";
        const string words = "Beschäftigungsverhältnisse quarterly-report https://example.com/a/b/c?id=123";
        const string longWord = "Donaudampfschifffahrtsgesellschaftskapitaenswitwenrentenversicherung";

        var result = ContentProvider.StripEncodedBlobs($"<vault>{blob}</vault> {words} {hash} {longWord}");

        Assert.DoesNotContain(blob[..40], result);
        Assert.DoesNotContain(hash, result);
        Assert.Contains("<vault>", result);
        Assert.Contains(words, result);
        Assert.Contains(longWord, result);
    }

    [Theory]
    [InlineData(null, false)]
    [InlineData("docs/sub", false)]
    [InlineData(".RECYCLE_BIN", true)]
    [InlineData("uid/.recycle_bin/old", true)]
    [InlineData("uid\\.RECYCLE_BIN", true)]
    public void IsRecycleBinScope_DetectsScopesInsideARecycleBin(string? prefix, bool expected)
        => Assert.Equal(expected, SearchServiceRouter.IsRecycleBinScope(prefix));

    [Fact]
    public void BuildSearchSql_FiltersRecycleBinUnlessRequested()
    {
        var (without, _) = SearchIndexRepository.BuildSearchSql(new[] { "report" }, null, null, 0, 50);
        var (with, _) = SearchIndexRepository.BuildSearchSql(new[] { "report" }, null, null, 0, 50, includeRecycleBin: true);

        Assert.Contains("!~*", without);
        Assert.DoesNotContain("!~*", with);
    }

    [Fact]
    public void EscapeLike_EscapesWildcardsAndTheEscapeCharacter()
        => Assert.Equal(@"50\%\_off\\", SearchIndexRepository.EscapeLike(@"50%_off\"));

    [Theory]
    [InlineData("Local", true, SearchEngine.Local)]
    [InlineData("filename", true, SearchEngine.Filename)]
    [InlineData("", true, SearchEngine.Elasticsearch)]
    [InlineData("", false, SearchEngine.Filename)]
    [InlineData("bogus", true, SearchEngine.Elasticsearch)]
    [InlineData("42", false, SearchEngine.Filename)]
    public void ParseEngine_FallsBackToLegacyFlagUntilAnEngineIsChosen(
        string stored, bool legacyElastic, SearchEngine expected)
        => Assert.Equal(expected, SearchConfigKeys.ParseEngine(stored, legacyElastic));

    [Fact]
    public async Task ContentProvider_UnknownBinary_YieldsNoContent()
    {
        // A SQLite database header: a known binary signature outside the skip list. Its MIME type
        // used to be returned as "content", so searching "application" matched such files.
        var bytes = Encoding.ASCII.GetBytes("SQLite format 3\0").Concat(new byte[84]).ToArray();

        Assert.Equal(string.Empty, await ContentProvider.GetContent(new MemoryStream(bytes), "blob.bin"));
    }

    [Fact]
    public async Task ContentProvider_PlainText_IsStillIndexed()
    {
        var bytes = Encoding.UTF8.GetBytes("hello search");

        Assert.Equal("hello search", await ContentProvider.GetContent(new MemoryStream(bytes), "a.txt"));
    }
}

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

        // "ünter" also as its decomposed form, which file names from macOS clients use.
        Assert.Equal(new[] { "report", "2024", "pdf", "ünter", "ünter" }, tokens);
    }

    [Fact]
    public void Tokenize_ComposesDecomposedInput_InsteadOfSplittingAtTheMark()
    {
        var tokens = LocalIndexSearchService.Tokenize("Kündigung");

        Assert.Equal(new[] { "kündigung", "kündigung" }, tokens);
    }

    [Theory]
    [InlineData("STRASSE", "strasse", "straße")]
    [InlineData("Straße", "straße", "strasse")]
    public void Tokenize_AddsTheSharpSSpellingVariant(string query, string first, string variant)
        => Assert.Equal(new[] { first, variant }, LocalIndexSearchService.Tokenize(query));

    [Fact]
    public void Mark_DropsLiteralMarkTagsFromTheText()
    {
        var marked = LocalIndexSearchService.Mark("a <mark>fake</mark> real", new[] { "real" });

        Assert.Equal("a fake <mark>real</mark>", marked);
    }

    [Fact]
    public void BuildSearchSql_ScansContentOncePerRowForMatching_AndNeverLowercasesWholeDocuments()
    {
        var (sql, parameters) = SearchIndexRepository.BuildSearchSql(
            new[] { "alpha", "beta", "ab" }, null, null, offset: 0, limit: 50);

        var values = parameters.Cast<NpgsqlParameter>().Select(p => p.Value).ToList();
        // One alternation over the content terms decides the match (index-backed, one pass).
        Assert.Contains("(alpha|beta)", values);
        Assert.DoesNotContain("\"Content\" ILIKE", sql);
        Assert.DoesNotContain("lower(d.\"Content\")", sql);
        Assert.Contains("regexp_instr", sql);
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
        // A hex dump longer than any hash (hashes themselves are kept, see ContentProviderTextDetectionTests).
        var hash = Convert.ToHexString(Enumerable.Range(0, 100).Select(i => (byte)(i * 41 + 3)).ToArray()).ToLowerInvariant();
        const string words = "Beschäftigungsverhältnisse quarterly-report https://example.com/a/b/c?id=123";
        const string longWord = "Donaudampfschifffahrtsgesellschaftskapitaenswitwenrentenversicherung";

        var result = ContentProvider.StripEncodedBlobs($"<vault>{blob}</vault> {words} {hash} {longWord}");

        Assert.DoesNotContain(blob[..40], result);
        Assert.DoesNotContain(hash, result);
        Assert.Contains("<vault>", result);
        Assert.Contains(words, result);
        Assert.Contains(longWord, result);
    }

    [Fact]
    public void StripEncodedBlobs_DropsBase64UrlPayloads_KeepsHyphenatedSlugs()
    {
        // base64url ('-' and '_' instead of '+' and '/'), e.g. JWTs and API tokens: the separators
        // split it into runs shorter than 60, so the plain base64 rule alone misses most of it.
        var bytes = Enumerable.Range(0, 300).Select(i => (byte)(i * 37 + 11)).ToArray();
        var b64url = Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        Assert.Contains('-', b64url);
        const string slug = "C--Users-Marco-source-repos-Kaimo-File-Server-CLAUDE-SPACE--worktrees-feature-2026";
        const string snake = "kaimo_file_server_search_index_rebuild_version_2026_10_15_final";

        var result = ContentProvider.StripEncodedBlobs($"token: {b64url} {slug} {snake}");

        Assert.DoesNotContain(b64url[..20], result);
        Assert.DoesNotContain(b64url[^20..], result);
        Assert.Contains("token:", result);
        Assert.Contains(slug, result);
        Assert.Contains(snake, result);
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

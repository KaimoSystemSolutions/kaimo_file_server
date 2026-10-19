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
    public void BuildSearchSql_MatchesContentThroughTheWordIndex_NeverReadsTheContent()
    {
        var (sql, parameters) = SearchIndexRepository.BuildSearchSql(
            new[] { "alpha", "beta", "ab" }, null, null, offset: 0, limit: 50);

        var values = parameters.Cast<NpgsqlParameter>().Select(p => p.Value).ToList();
        // Whole words are tsquery literals over the stored word vector; word parts come from
        // the term dictionary. The (TOASTed) content itself is never touched by the ranking.
        Assert.Contains("'alpha'", values);
        Assert.Contains("'beta'", values);
        Assert.DoesNotContain("'ab'", values);
        Assert.Contains("\"ContentTsv\" @@", sql);
        Assert.Contains("search_terms", sql);
        Assert.DoesNotContain("\"Content\"", sql);
        // Excerpts are fetched separately for the shown hits only (GetExcerptsAsync).
        Assert.DoesNotContain("regexp_instr", sql);
    }

    [Fact]
    public void ContentWords_SplitLikeQueryTerms_LowercasedDistinct_WithoutWordsTooShortToMatch()
    {
        Assert.Equal(["max", "firma", "kündigung", "x2024"],
            SearchIndexRepository.ContentWords("Max@Firma.de, firma FIRMA ab Kündigung x2024 z"));
        Assert.Empty(SearchIndexRepository.ContentWords(""));
    }

    [Fact]
    public void ContentWords_StayBelowTheWordVectorLimit_KeepingTheLeadingWords()
    {
        // 250,000 distinct 9-byte words: 2.25 MB, more than a tsvector's 1 MB of lexemes.
        static string Word(int i) => new([(char)(0x4E00 + i % 20000), (char)(0x4E00 + i / 20000), '一']);
        var words = SearchIndexRepository.ContentWords(string.Join(' ', Enumerable.Range(0, 250_000).Select(Word)));

        Assert.Equal(1_000_000 / 9, words.Length);
        Assert.Equal(Word(0), words[0]);
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
        Assert.DoesNotContain("'ab'", values);
        Assert.Contains("'report'", values);
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
    public void BuildSearchSql_TwoCharTermsOnlyScore_WhenALongerTermIsPresent()
    {
        // "kw" -> @p0 and "42" -> @p1: a pattern without a full trigram cannot use the index,
        // and as a candidate source it would force a sequential scan over every document.
        var (sql, _) = SearchIndexRepository.BuildSearchSql(new[] { "kw", "42", "bericht" }, null, null, 0, 50);

        var candidates = Candidates(sql);
        Assert.DoesNotMatch(@"@p0\b", candidates);
        Assert.DoesNotMatch(@"@p1\b", candidates);
        Assert.Matches(@"""FileName"" ILIKE @p\d+", candidates);
        // They still rank: a name containing them comes first.
        var ranking = sql[sql.LastIndexOf("ORDER BY", StringComparison.Ordinal)..];
        Assert.Matches(@"CASE WHEN d.""FileName"" ILIKE @p0\b", ranking);
        Assert.Matches(@"CASE WHEN d.""FileName"" ILIKE @p1\b", ranking);
    }

    [Fact]
    public void BuildSearchSql_TwoCharTermsAlone_StillMatchFileNames()
    {
        var (sql, _) = SearchIndexRepository.BuildSearchSql(new[] { "ab", "cd" }, null, null, 0, 50);

        var candidates = Candidates(sql);
        Assert.Matches(@"ILIKE @p0\b", candidates);
        Assert.Matches(@"ILIKE @p1\b", candidates);
        Assert.DoesNotContain("Content", candidates);
    }

    /// <summary>The candidate part of the statement: everything before the per-document aggregation.</summary>
    private static string Candidates(string sql) => sql[..sql.IndexOf("hits AS", StringComparison.Ordinal)];

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

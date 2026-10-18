using System.Text;
using System.Text.RegularExpressions;
using Kaimo_File_Server.Core.Helpers;
using Kaimo_File_Server.Core.Repositories;
using Kaimo_File_Server.Infrastructure.Persistence;
using Kaimo_File_Server.Search;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Kaimo_File_Server.Infrastructure.Repositories;

/// <summary>
/// PostgreSQL implementation of <see cref="ISearchIndexRepository"/> over <c>search_documents</c>
/// and its word dictionary <c>search_terms</c>. Raw SQL throughout: ranking, prefix rewrites and
/// the id recomputation (<c>md5()</c>) are single statements that EF LINQ cannot express. All
/// values are bound as parameters. Stateless over the context factory, so it is registered as a
/// singleton.
///
/// Content search is an inverted index, not a scan: each document stores its distinct words as a
/// GIN-indexed tsvector, and <c>search_terms</c> holds every word any document contains, trigram-
/// indexed. A search term is first resolved to the dictionary words containing it, then those
/// words are looked up in the word index — the content itself is only read for excerpts.
/// </summary>
public sealed partial class SearchIndexRepository : ISearchIndexRepository
{
    // Characters of context kept on each side of the first content match in the excerpt.
    private const int FragmentLead = 40;
    private const int FragmentLength = 120;

    // Leading content characters searched for the excerpt before the whole content is.
    private const int ExcerptWindowChars = 20_000;

    // Content matching starts at three characters: a trigram index cannot serve a shorter
    // pattern, neither on file names nor on the word dictionary.
    internal const int MinContentTokenLength = 3;

    // Dictionary words one search term expands to, shortest first (closest to the term; the
    // term itself as a whole word always fits). A document whose only matching words lie beyond
    // the cap is missed — only possible for very short, unselective terms, whose hits exceed
    // SearchLimits.MaxRawScan anyway.
    // ponytail: fixed cap; raise it or fall back to a content scan if such misses show up.
    internal const int MaxTermsPerToken = 5_000;

    // A tsvector holds at most 1 MB of lexemes (PostgreSQL's MAXSTRPOS, 1,048,575 bytes). Words
    // past this budget are left out of the word vector; only documents with that much distinct
    // vocabulary are affected, and their excerpts still come from the whole content.
    private const int MaxWordBytes = 1_000_000;

    // PostgreSQL ignores longer lexemes (a tsvector entry is at most 2 KB).
    private const int MaxLexemeBytes = 2_046;

    private readonly IDbContextFactory<ApplicationDbContext> _dbFactory;

    public SearchIndexRepository(IDbContextFactory<ApplicationDbContext> dbFactory)
        => _dbFactory = dbFactory;

    /// <summary>
    /// One statement writes the document, its word vector and its new dictionary words, so they
    /// commit together (<see cref="CompactAsync"/> relies on that). Words are inserted in sorted
    /// order, so concurrent writers sharing words cannot deadlock on the dictionary.
    /// </summary>
    public async Task UpsertAsync(
        FileDocument doc, DateTime? fileModifiedUtc, DateTime indexedUtc, CancellationToken ct = default)
    {
        var words = ContentWords(doc.Content);
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        await db.Database.ExecuteSqlAsync($"""
            WITH doc AS (
                INSERT INTO search_documents
                    ("Id", "FileName", "ShareName", "AbsolutePath", "SharePath", "FileType",
                     "IsDirectory", "SizeBytes", "Content", "ContentTsv", "FileModifiedUtc", "CreatedUtc", "IndexedUtc")
                VALUES ({doc.Id}, {doc.FileName}, {doc.ShareName}, {doc.AbsolutePath}, {doc.SharePath}, {doc.FileType},
                        {doc.IsDirectory}, {doc.FileSizeBytes}, {doc.Content}, array_to_tsvector({words}),
                        {fileModifiedUtc}, {doc.Created}, {indexedUtc})
                ON CONFLICT ("Id") DO UPDATE SET
                    "FileName" = EXCLUDED."FileName",
                    "ShareName" = EXCLUDED."ShareName",
                    "AbsolutePath" = EXCLUDED."AbsolutePath",
                    "SharePath" = EXCLUDED."SharePath",
                    "FileType" = EXCLUDED."FileType",
                    "IsDirectory" = EXCLUDED."IsDirectory",
                    "SizeBytes" = EXCLUDED."SizeBytes",
                    "Content" = EXCLUDED."Content",
                    "ContentTsv" = EXCLUDED."ContentTsv",
                    "FileModifiedUtc" = EXCLUDED."FileModifiedUtc",
                    "IndexedUtc" = EXCLUDED."IndexedUtc"
                RETURNING "ContentTsv")
            INSERT INTO search_terms ("Term")
            SELECT w.lexeme FROM doc, unnest(doc."ContentTsv") w
            ORDER BY 1
            ON CONFLICT DO NOTHING
            """, ct);
    }

    public async Task<bool> TouchIfUnchangedAsync(
        string id, long sizeBytes, DateTime fileModifiedUtc, DateTime indexedUtc, CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        return await db.Database.ExecuteSqlAsync($"""
            UPDATE search_documents SET "IndexedUtc" = {indexedUtc}
            WHERE "Id" = {id} AND "SizeBytes" = {sizeBytes} AND "FileModifiedUtc" = {fileModifiedUtc}
            """, ct) == 1;
    }

    public Task ResetContentStampsAsync(CancellationToken ct = default)
        => ResetContentStampsAsync(ResetBatchSize, ct);

    // Rows per reset statement. A row update is rarely HOT here (bulk-filled pages), so each one
    // also re-inserts the row into the trigram GIN index; one statement over the whole table
    // exceeded the command timeout on large indexes.
    private const int ResetBatchSize = 1_000;

    /// <summary>
    /// Clears the stamps in primary-key ranges, one short statement each: no single long
    /// transaction holds row locks against live indexing, and an interrupted reset resumes
    /// cheaply (already cleared rows are skipped by the IS NOT NULL filter).
    /// </summary>
    internal async Task ResetContentStampsAsync(int batchSize, CancellationToken ct)
    {
        var after = string.Empty;
        while (true)
        {
            await using var db = await _dbFactory.CreateDbContextAsync(ct);
            var upper = (await db.Database.SqlQuery<string?>($"""
                SELECT max("Id") AS "Value" FROM (
                    SELECT "Id" FROM search_documents WHERE "Id" > {after} ORDER BY "Id" LIMIT {batchSize}) b
                """).ToListAsync(ct)).SingleOrDefault();
            if (upper is null)
                return;
            await db.Database.ExecuteSqlAsync($"""
                UPDATE search_documents SET "FileModifiedUtc" = NULL
                WHERE "Id" > {after} AND "Id" <= {upper} AND "FileModifiedUtc" IS NOT NULL
                """, ct);
            after = upper;
        }
    }

    // ponytail: VACUUM FULL locks the table exclusively while it rewrites it (seconds for a
    // typical index, minutes in the GB range; searches wait meanwhile). Only run after a version
    // rebuild; switch to pg_repack or plain VACUUM if that lock becomes a problem.
    // ponytail: the word dictionary only shrinks here; prune it periodically if it grows too far.
    /// <summary>
    /// Also rebuilds the word dictionary from the stored word vectors. Deletes and content changes
    /// never remove dictionary words (they only cost a few extra lookups), so this is where words
    /// no document uses any more go. The TRUNCATE lock holds writers back until the new dictionary
    /// is committed, and a writer adds its words in the same statement as its document, so no
    /// word is lost to a concurrent write.
    /// </summary>
    public async Task CompactAsync(CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        // No command timeout: rewriting a large index outlasts the default and was aborted
        // (the token still cancels it).
        db.Database.SetCommandTimeout(0);
        await using (var tx = await db.Database.BeginTransactionAsync(ct))
        {
            await db.Database.ExecuteSqlRawAsync("TRUNCATE search_terms", ct);
            await db.Database.ExecuteSqlRawAsync("""
                INSERT INTO search_terms ("Term")
                SELECT word FROM ts_stat('SELECT "ContentTsv" FROM search_documents')
                """, ct);
            await tx.CommitAsync(ct);
        }
        // VACUUM cannot run inside a transaction; ExecuteSql issues it as a single statement.
        await db.Database.ExecuteSqlRawAsync("VACUUM (FULL, ANALYZE) search_documents, search_terms", ct);
    }

    public async Task TouchAsync(string id, DateTime indexedUtc, CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        await db.Database.ExecuteSqlAsync(
            $"""UPDATE search_documents SET "IndexedUtc" = {indexedUtc} WHERE "Id" = {id}""", ct);
    }

    public async Task DeleteAsync(string id, CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        await db.Database.ExecuteSqlAsync($"""DELETE FROM search_documents WHERE "Id" = {id}""", ct);
    }

    public async Task<int> DeleteTreeAsync(string absolutePath, CancellationToken ct = default)
    {
        var root = TrimSeparator(absolutePath);
        var children = EscapeLike(root + Path.DirectorySeparatorChar) + "%";
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        return await db.Database.ExecuteSqlAsync($"""
            DELETE FROM search_documents
            WHERE "AbsolutePath" = {root} OR "AbsolutePath" LIKE {children}
            """, ct);
    }

    /// <summary>
    /// One transaction: drop whatever already sits at the target (an overwrite would otherwise
    /// collide on the recomputed id), then rewrite the moved rows in place. Replaying it after a
    /// lost commit acknowledgement is harmless — the old paths are gone, so it matches nothing.
    /// </summary>
    public Task<int> MoveAsync(
        string oldAbsolutePath, string newAbsolutePath,
        string newShareName, string newSharePath, DateTime indexedUtc, CancellationToken ct = default)
    {
        var oldRoot = TrimSeparator(oldAbsolutePath);
        var newRoot = TrimSeparator(newAbsolutePath);
        var sep = Path.DirectorySeparatorChar.ToString();
        var oldChildren = EscapeLike(oldRoot + sep) + "%";
        var newChildren = EscapeLike(newRoot + sep) + "%";
        var newName = Path.GetFileName(newRoot);
        var newType = Path.GetExtension(newRoot).TrimStart('.');

        return _dbFactory.ExecuteResilientAsync(async db =>
        {
            await using var tx = await db.Database.BeginTransactionAsync(ct);
            await db.Database.ExecuteSqlAsync($"""
                DELETE FROM search_documents
                WHERE ("AbsolutePath" = {newRoot} OR "AbsolutePath" LIKE {newChildren})
                  AND NOT ("AbsolutePath" = {oldRoot} OR "AbsolutePath" LIKE {oldChildren})
                """, ct);
            // The remainder after the old root is cut in SQL by char_length (code points): the
            // .NET string length counts UTF-16 units and is off for e.g. emoji in folder names.
            // IndexedUtc is refreshed so a reindex pass running meanwhile does not sweep the
            // moved rows (it walked the old paths, not the new ones).
            var moved = await db.Database.ExecuteSqlAsync($"""
                UPDATE search_documents SET
                    "Id"           = md5({newRoot} || substring("AbsolutePath" from char_length({oldRoot}) + 1)),
                    "AbsolutePath" = {newRoot} || substring("AbsolutePath" from char_length({oldRoot}) + 1),
                    "SharePath"    = {newSharePath} || substring("AbsolutePath" from char_length({oldRoot}) + 1),
                    "ShareName"    = {newShareName},
                    "FileName"     = CASE WHEN "AbsolutePath" = {oldRoot} THEN {newName} ELSE "FileName" END,
                    "FileType"     = CASE WHEN "AbsolutePath" = {oldRoot} AND NOT "IsDirectory"
                                          THEN {newType} ELSE "FileType" END,
                    "IndexedUtc"   = {indexedUtc}
                WHERE "AbsolutePath" = {oldRoot} OR "AbsolutePath" LIKE {oldChildren}
                """, ct);
            await tx.CommitAsync(ct);
            return moved;
        }, ct);
    }

    public async Task<List<FileDocument>> SearchAsync(
        IReadOnlyList<string> tokens, string? shareName, string? pathPrefix,
        int offset, int limit, bool includeRecycleBin = false, CancellationToken ct = default)
    {
        if (tokens.Count == 0)
            return new List<FileDocument>();

        var (sql, parameters) = BuildSearchSql(tokens, shareName, pathPrefix, offset, limit, includeRecycleBin);

        // The planner ignores the decompression of each document's word vector, so on small or
        // mid-sized tables it picks a sequential scan that reads all of them (measured with the
        // former content regex: 343 ms vs. a few ms over the index). Discouraging seq scans for
        // this one read-only statement keeps it on the indexes; a query without any index path
        // (two-character terms only) still falls back to a scan.
        var rows = await _dbFactory.ExecuteResilientAsync(async db =>
        {
            await using var tx = await db.Database.BeginTransactionAsync(ct);
            await db.Database.ExecuteSqlRawAsync("SET LOCAL enable_seqscan = off", ct);
            var result = await db.Database.SqlQueryRaw<SearchHitRow>(sql, parameters).ToListAsync(ct);
            await tx.CommitAsync(ct);
            return result;
        }, ct);

        return rows.Select(r => new FileDocument
        {
            Id = r.Id,
            FileName = r.FileName,
            ShareName = r.ShareName,
            AbsolutePath = r.AbsolutePath,
            SharePath = r.SharePath,
            FileType = r.FileType,
            FileSizeBytes = r.SizeBytes,
            IsDirectory = r.IsDirectory,
            Created = r.CreatedUtc,
            Modified = r.IndexedUtc
        }).ToList();
    }

    /// <summary>
    /// The excerpt position comes from regexp_instr at the first match of any content term;
    /// documents without a content match get no entry. PostgreSQL's regex converts its whole
    /// input before matching (measured ~3 ms per 500 KB document), so the leading
    /// <see cref="ExcerptWindowChars"/> characters — decompressed alone — are searched first and
    /// the whole content only when they hold no match. OFFSET 0 keeps the lateral subquery from
    /// being inlined, which would run the regex twice per row (filter and fragment).
    /// </summary>
    public async Task<Dictionary<string, string>> GetExcerptsAsync(
        IReadOnlyCollection<string> ids, IReadOnlyList<string> tokens, CancellationToken ct = default)
    {
        var alternation = ContentAlternation(tokens);
        if (ids.Count == 0 || alternation is null)
            return new Dictionary<string, string>();

        var idArray = ids.ToArray();
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var rows = await db.Database.SqlQuery<ExcerptRow>($"""
            SELECT d."Id", substring(d."Content" from greatest(p.pos - {FragmentLead}, 1) for {FragmentLength}) AS "Fragment"
            FROM search_documents d
            CROSS JOIN LATERAL (
                SELECT coalesce(nullif(regexp_instr(left(d."Content", {ExcerptWindowChars}), {alternation}, 1, 1, 0, 'i'), 0),
                                regexp_instr(d."Content", {alternation}, 1, 1, 0, 'i')) AS pos
                OFFSET 0) p
            WHERE d."Id" = ANY({idArray}) AND p.pos > 0
            """).ToListAsync(ct);
        return rows.ToDictionary(r => r.Id, r => r.Fragment);
    }

    /// <summary>
    /// One regex alternation over the content terms (three characters and up), or null when
    /// there are none. Tokens are letters, marks and digits only (see
    /// LocalIndexSearchService.Tokenize), so they are safe inside a regex.
    /// </summary>
    private static string? ContentAlternation(IReadOnlyList<string> tokens)
    {
        var terms = tokens.Where(t => t.Length >= MinContentTokenLength).ToList();
        return terms.Count == 0 ? null : "(" + string.Join("|", terms) + ")";
    }

    /// <summary>
    /// Builds the ranked search statement. Each token is OR-combined (like the Elasticsearch
    /// match query) and scored with the Elasticsearch boosts: name substring 4, whole word in the
    /// content 2, content word containing the token 1. Candidates come from index lookups only
    /// (file-name trigrams, word index), each scoring its content points; they are summed per
    /// document. Folders come first, then name points, then content points, so any file-name match ranks
    /// above every content-only match (like the name tier in Elasticsearch). Neither matching nor ranking
    /// reads the content; excerpts are fetched by <see cref="GetExcerptsAsync"/> for the shown
    /// hits only. Word parts match within one word, not across word boundaries (like n-grams).
    /// Two-character terms have no trigram, so they cannot use an index: with a longer term
    /// present they only rank, otherwise they match file names by a sequential scan.
    /// Tokens are letters, marks and digits only (see LocalIndexSearchService.Tokenize), so they
    /// are safe as tsquery literals.
    /// </summary>
    internal static (string Sql, object[] Parameters) BuildSearchSql(
        IReadOnlyList<string> tokens, string? shareName, string? pathPrefix, int offset, int limit,
        bool includeRecycleBin = false)
    {
        var parameters = new List<object>();
        string Param(object value)
        {
            var name = "@p" + parameters.Count;
            parameters.Add(new NpgsqlParameter(name, value));
            return name;
        }

        var nameMatch = new List<string>();
        var nameScore = new List<string>();
        var contentArms = new List<string>();
        bool anyLongTerm = tokens.Any(t => t.Length >= MinContentTokenLength);
        foreach (var token in tokens)
        {
            var like = Param("%" + EscapeLike(token) + "%");
            if (!anyLongTerm || token.Length >= MinContentTokenLength)
                nameMatch.Add($"""d."FileName" ILIKE {like}""");
            nameScore.Add($"""CASE WHEN d."FileName" ILIKE {like} THEN 4 ELSE 0 END""");

            if (token.Length < MinContentTokenLength)
                continue;

            // Any word containing the token: its dictionary words as one OR tsquery, each quoted
            // with quotes and backslashes doubled as tsquery input requires. No word -> NULL -> no rows.
            contentArms.Add($"""
                SELECT d."Id", 1 FROM search_documents d WHERE d."ContentTsv" @@ (
                    SELECT CAST(string_agg('''' || replace(replace(t."Term", '\', '\\'), '''', '''''') || '''', ' | ') AS tsquery)
                    FROM (SELECT "Term" FROM search_terms WHERE "Term" LIKE {like}
                          ORDER BY length("Term"), "Term" LIMIT {MaxTermsPerToken}) t)
                """);
            // The token as a whole word.
            contentArms.Add($"""
                SELECT d."Id", 2 FROM search_documents d WHERE d."ContentTsv" @@ CAST({Param("'" + token + "'")} AS tsquery)
                """);
        }

        var filters = new List<string>();
        if (!string.IsNullOrWhiteSpace(shareName))
        {
            filters.Add($"""d."ShareName" = {Param(shareName)}""");

            // Same scope rule as the Elasticsearch keyword filter: the folder itself or anything
            // strictly below it, so a sibling like "reports2" is not swept in by "reports".
            var prefix = (pathPrefix ?? string.Empty).Replace('\\', '/').Trim('/');
            if (prefix.Length > 0)
                filters.Add($"""
                    (d."SharePath" = {Param(prefix)} OR d."SharePath" LIKE {Param(EscapeLike(prefix + "/") + "%")})
                    """);
        }

        // Recycle-bin documents: any ".RECYCLE_BIN" segment (only the share's/home's own bin is
        // ever indexed), case-insensitive like ShareEntryPolicy.
        if (!includeRecycleBin)
            filters.Add($"""
                d."SharePath" !~* {Param("(^|/)[.]" + ShareEntryPolicy.RecycleBinName[1..] + "(/|$)")}
                """);

        var arms = new List<string>
        {
            $"""SELECT d."Id", 0 AS p FROM search_documents d WHERE {string.Join(" OR ", nameMatch)}"""
        };
        arms.AddRange(contentArms);
        var where = filters.Count == 0 ? "" : "WHERE " + string.Join(" AND ", filters);

        var sql = $"""
            WITH pts AS (
            {string.Join("\nUNION ALL\n", arms)}
            ),
            hits AS (SELECT "Id", sum(p) AS p FROM pts GROUP BY "Id")
            SELECT d."Id", d."FileName", d."ShareName", d."AbsolutePath", d."SharePath", d."FileType",
                   d."SizeBytes", d."IsDirectory", d."CreatedUtc", d."IndexedUtc"
            FROM hits h
            JOIN search_documents d ON d."Id" = h."Id"
            {where}
            ORDER BY d."IsDirectory" DESC, {string.Join(" + ", nameScore)} DESC, h.p DESC, d."Id"
            OFFSET {Param(offset)} LIMIT {Param(limit)}
            """;
        return (sql, parameters.ToArray());
    }

    public async Task<int> SweepAsync(
        IReadOnlyCollection<string> shareNames, DateTime indexedBefore, CancellationToken ct = default)
    {
        if (shareNames.Count == 0)
            return 0;

        var names = shareNames.ToArray();
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        return await db.Database.ExecuteSqlAsync($"""
            DELETE FROM search_documents
            WHERE "ShareName" = ANY({names}) AND "IndexedUtc" < {indexedBefore}
            """, ct);
    }

    public async Task<SearchIndexStats> GetStatsAsync(CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var stats = await db.Database.SqlQueryRaw<StatsRow>("""
            SELECT count(*) AS "Documents",
                   pg_total_relation_size('search_documents') + pg_total_relation_size('search_terms') AS "Bytes"
            FROM search_documents
            """).SingleAsync(ct);
        return new SearchIndexStats(stats.Documents, stats.Bytes);
    }

    /// <summary>Escapes the LIKE wildcards so user input always matches literally (PostgreSQL's default LIKE escape is '\').</summary>
    /// <summary>
    /// The distinct words of a document as its word vector stores them: split and lowercased
    /// exactly like the query terms (LocalIndexSearchService.Tokenize), so a term and a word
    /// compare equal. Words under <see cref="MinContentTokenLength"/> characters are left out —
    /// no content term can match inside them. Built here rather than by to_tsvector, whose parser
    /// keeps addresses and URLs as one word and whose size limit counts positions as well.
    /// Leading words win once <see cref="MaxWordBytes"/> is reached.
    /// </summary>
    internal static string[] ContentWords(string content)
    {
        var words = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        int bytes = 0;
        foreach (Match m in WordRegex().Matches(content))
        {
            if (m.Length < MinContentTokenLength)
                continue;
            var word = m.Value.ToLowerInvariant();
            int size = Encoding.UTF8.GetByteCount(word);
            if (size > MaxLexemeBytes || !seen.Add(word))
                continue;
            if ((bytes += size) > MaxWordBytes)
                break;
            words.Add(word);
        }
        return words.ToArray();
    }

    // Same pattern as LocalIndexSearchService.TokenRegex.
    [GeneratedRegex(@"[\p{L}\p{M}\p{N}]+")]
    private static partial Regex WordRegex();

    internal static string EscapeLike(string value)
        => value.Replace(@"\", @"\\").Replace("%", @"\%").Replace("_", @"\_");

    private static string TrimSeparator(string path)
        => path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

    internal sealed class SearchHitRow
    {
        public string Id { get; set; } = string.Empty;
        public string FileName { get; set; } = string.Empty;
        public string ShareName { get; set; } = string.Empty;
        public string AbsolutePath { get; set; } = string.Empty;
        public string SharePath { get; set; } = string.Empty;
        public string FileType { get; set; } = string.Empty;
        public long SizeBytes { get; set; }
        public bool IsDirectory { get; set; }
        public DateTime CreatedUtc { get; set; }
        public DateTime IndexedUtc { get; set; }
    }

    internal sealed class ExcerptRow
    {
        public string Id { get; set; } = string.Empty;
        public string Fragment { get; set; } = string.Empty;
    }

    internal sealed class StatsRow
    {
        public long Documents { get; set; }
        public long Bytes { get; set; }
    }
}

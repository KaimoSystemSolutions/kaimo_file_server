using System.Text;
using Kaimo_File_Server.Core.Helpers;
using Kaimo_File_Server.Core.Repositories;
using Kaimo_File_Server.Infrastructure.Persistence;
using Kaimo_File_Server.Search;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Kaimo_File_Server.Infrastructure.Repositories;

/// <summary>
/// PostgreSQL implementation of <see cref="ISearchIndexRepository"/> over <c>search_documents</c>.
/// Raw SQL throughout: ranking, prefix rewrites and the id recomputation (<c>md5()</c>) are
/// single statements that EF LINQ cannot express. All values are bound as parameters.
/// Stateless over the context factory, so it is registered as a singleton.
/// </summary>
public sealed class SearchIndexRepository : ISearchIndexRepository
{
    // Characters of context kept on each side of the first content match in the excerpt.
    private const int FragmentLead = 40;
    private const int FragmentLength = 120;

    // Content-substring matching starts at three characters: a trigram index cannot serve a
    // shorter pattern, and a full scan of every document's content would exceed the search timeout.
    internal const int MinContentTokenLength = 3;

    // Leading content characters the per-term ranking looks at. Whether a document matches is
    // still decided over its whole content; only the order among matches ignores what lies deeper.
    // ponytail: fixed window; a stored rank vector (tsvector) would rank on everything.
    private const int ScoreWindowChars = 20_000;

    private readonly IDbContextFactory<ApplicationDbContext> _dbFactory;

    public SearchIndexRepository(IDbContextFactory<ApplicationDbContext> dbFactory)
        => _dbFactory = dbFactory;

    public async Task UpsertAsync(
        FileDocument doc, DateTime? fileModifiedUtc, DateTime indexedUtc, CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        await db.Database.ExecuteSqlAsync($"""
            INSERT INTO search_documents
                ("Id", "FileName", "ShareName", "AbsolutePath", "SharePath", "FileType",
                 "IsDirectory", "SizeBytes", "Content", "FileModifiedUtc", "CreatedUtc", "IndexedUtc")
            VALUES ({doc.Id}, {doc.FileName}, {doc.ShareName}, {doc.AbsolutePath}, {doc.SharePath}, {doc.FileType},
                    {doc.IsDirectory}, {doc.FileSizeBytes}, {doc.Content}, {fileModifiedUtc}, {doc.Created}, {indexedUtc})
            ON CONFLICT ("Id") DO UPDATE SET
                "FileName" = EXCLUDED."FileName",
                "ShareName" = EXCLUDED."ShareName",
                "AbsolutePath" = EXCLUDED."AbsolutePath",
                "SharePath" = EXCLUDED."SharePath",
                "FileType" = EXCLUDED."FileType",
                "IsDirectory" = EXCLUDED."IsDirectory",
                "SizeBytes" = EXCLUDED."SizeBytes",
                "Content" = EXCLUDED."Content",
                "FileModifiedUtc" = EXCLUDED."FileModifiedUtc",
                "IndexedUtc" = EXCLUDED."IndexedUtc"
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
    public async Task CompactAsync(CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        // No command timeout: rewriting a large index outlasts the default and was aborted
        // (the token still cancels it).
        db.Database.SetCommandTimeout(0);
        // VACUUM cannot run inside a transaction; ExecuteSql issues it as a single statement.
        await db.Database.ExecuteSqlRawAsync("VACUUM (FULL, ANALYZE) search_documents", ct);
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

        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var rows = await db.Database.SqlQueryRaw<SearchHitRow>(sql, parameters).ToListAsync(ct);

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
            Modified = r.IndexedUtc,
            HighlightSnippet = r.Fragment
        }).ToList();
    }

    /// <summary>
    /// Builds the ranked search statement. Each token is OR-combined (like the Elasticsearch
    /// match query) and scored with the Elasticsearch boosts: name substring 4, whole word in the
    /// content 2, content substring 1. The inner query ranks and pages over ids only; the
    /// excerpt is computed afterwards for the page's rows alone.
    /// Cost model (measured): a case-insensitive pass over the content is the expensive part —
    /// ILIKE ~2x a regex pass, lower() of a whole document as much again. So whether the content
    /// matches at all is one regex alternation over every term (pg_trgm serves it from the index
    /// for selective terms), the per-term ranking only looks at the first
    /// <see cref="ScoreWindowChars"/> characters (PostgreSQL decompresses just that slice), and
    /// the excerpt position comes from one regexp_instr that stops at the first match.
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

        var match = new List<string>();
        var score = new List<string>();
        var contentTerms = new List<string>();
        foreach (var token in tokens)
        {
            var like = Param("%" + EscapeLike(token) + "%");
            match.Add($"""d."FileName" ILIKE {like}""");
            score.Add($"""CASE WHEN d."FileName" ILIKE {like} THEN 4 ELSE 0 END""");

            if (token.Length < MinContentTokenLength)
                continue;

            // Tokens are letters, marks and digits only (see LocalIndexSearchService.Tokenize), so
            // they are safe inside a regex; \m and \M are PostgreSQL's word-start/word-end anchors.
            contentTerms.Add(token);
            score.Add($"""CASE WHEN w.lead ~* {Param(token)} THEN 1 ELSE 0 END""");
            score.Add($"""CASE WHEN w.lead ~* {Param(@"\m" + token + @"\M")} THEN 2 ELSE 0 END""");
        }

        string? anyContentTerm = null;
        if (contentTerms.Count > 0)
        {
            anyContentTerm = Param("(" + string.Join("|", contentTerms) + ")");
            match.Add($"""d."Content" ~* {anyContentTerm}""");
        }

        var where = new StringBuilder($"({string.Join(" OR ", match)})");
        if (!string.IsNullOrWhiteSpace(shareName))
        {
            where.Append($""" AND d."ShareName" = {Param(shareName)}""");

            // Same scope rule as the Elasticsearch keyword filter: the folder itself or anything
            // strictly below it, so a sibling like "reports2" is not swept in by "reports".
            var prefix = (pathPrefix ?? string.Empty).Replace('\\', '/').Trim('/');
            if (prefix.Length > 0)
                where.Append($"""
                     AND (d."SharePath" = {Param(prefix)} OR d."SharePath" LIKE {Param(EscapeLike(prefix + "/") + "%")})
                    """);
        }

        // Recycle-bin documents: any ".RECYCLE_BIN" segment (only the share's/home's own bin is
        // ever indexed), case-insensitive like ShareEntryPolicy.
        if (!includeRecycleBin)
            where.Append($"""
                 AND d."SharePath" !~* {Param("(^|/)[.]" + ShareEntryPolicy.RecycleBinName[1..] + "(/|$)")}
                """);

        var position = anyContentTerm is null
            ? "0"
            : $"""regexp_instr(d."Content", {anyContentTerm}, 1, 1, 0, 'i')""";

        // The ranking window is cut once per matching row (OFFSET 0 keeps the subquery from being
        // inlined, which would repeat the cut for every term).
        var window = anyContentTerm is null
            ? ""
            : $"""CROSS JOIN LATERAL (SELECT left(d."Content", {ScoreWindowChars}) AS lead OFFSET 0) w""";

        var sql = $"""
            WITH hits AS (
                SELECT d."Id", ({string.Join(" + ", score)}) AS score
                FROM search_documents d
                {window}
                WHERE {where}
                ORDER BY score DESC, d."Id"
                OFFSET {Param(offset)} LIMIT {Param(limit)}
            )
            SELECT d."Id", d."FileName", d."ShareName", d."AbsolutePath", d."SharePath", d."FileType",
                   d."SizeBytes", d."IsDirectory", d."CreatedUtc", d."IndexedUtc",
                   CASE WHEN p.pos = 0 THEN NULL
                        ELSE substring(d."Content" from greatest(p.pos - {FragmentLead}, 1) for {FragmentLength}) END
                   AS "Fragment"
            FROM hits h
            JOIN search_documents d ON d."Id" = h."Id"
            CROSS JOIN LATERAL (SELECT {position} AS pos) p
            ORDER BY h.score DESC, h."Id"
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
            SELECT count(*) AS "Documents", pg_total_relation_size('search_documents') AS "Bytes"
            FROM search_documents
            """).SingleAsync(ct);
        return new SearchIndexStats(stats.Documents, stats.Bytes);
    }

    /// <summary>Escapes the LIKE wildcards so user input always matches literally (PostgreSQL's default LIKE escape is '\').</summary>
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
        public string? Fragment { get; set; }
    }

    internal sealed class StatsRow
    {
        public long Documents { get; set; }
        public long Bytes { get; set; }
    }
}

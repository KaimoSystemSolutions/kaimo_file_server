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

    public async Task ResetContentStampsAsync(CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        await db.Database.ExecuteSqlAsync(
            $"""UPDATE search_documents SET "FileModifiedUtc" = NULL WHERE "FileModifiedUtc" IS NOT NULL""", ct);
    }

    // ponytail: VACUUM FULL locks the table exclusively while it rewrites it (seconds for a
    // typical index, searches wait meanwhile). Only run after a version rebuild; switch to
    // pg_repack or plain VACUUM if indexes grow into the GB range.
    public async Task CompactAsync(CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
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
    /// excerpt is computed afterwards for the page's rows alone, so no full content is
    /// lowercased or transferred for non-returned hits.
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
        var positions = new List<string>();
        foreach (var token in tokens)
        {
            var like = Param("%" + EscapeLike(token) + "%");
            match.Add($"""d."FileName" ILIKE {like}""");
            score.Add($"""CASE WHEN d."FileName" ILIKE {like} THEN 4 ELSE 0 END""");

            if (token.Length < MinContentTokenLength)
                continue;

            match.Add($"""d."Content" ILIKE {like}""");
            score.Add($"""CASE WHEN d."Content" ILIKE {like} THEN 1 ELSE 0 END""");
            // Tokens are letters/digits only (see LocalIndexSearchService.Tokenize), so they are
            // safe inside a regex; \m and \M are PostgreSQL's word-start/word-end anchors.
            score.Add($"""CASE WHEN d."Content" ~* {Param(@"\m" + token + @"\M")} THEN 2 ELSE 0 END""");
            positions.Add($"NULLIF(strpos(l.lc, {Param(token.ToLowerInvariant())}), 0)");
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

        var excerpt = positions.Count == 0
            ? "NULL::text"
            : $"""
              CASE WHEN p.pos IS NULL THEN NULL
                   ELSE substring(d."Content" from greatest(p.pos - {FragmentLead}, 1) for {FragmentLength}) END
              """;
        var position = positions.Count == 0 ? "NULL::int" : $"COALESCE({string.Join(", ", positions)})";

        var sql = $"""
            WITH hits AS (
                SELECT d."Id", ({string.Join(" + ", score)}) AS score
                FROM search_documents d
                WHERE {where}
                ORDER BY score DESC, d."Id"
                OFFSET {Param(offset)} LIMIT {Param(limit)}
            )
            SELECT d."Id", d."FileName", d."ShareName", d."AbsolutePath", d."SharePath", d."FileType",
                   d."SizeBytes", d."IsDirectory", d."CreatedUtc", d."IndexedUtc",
                   {excerpt} AS "Fragment"
            FROM hits h
            JOIN search_documents d ON d."Id" = h."Id"
            CROSS JOIN LATERAL (SELECT lower(d."Content") AS lc) l
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

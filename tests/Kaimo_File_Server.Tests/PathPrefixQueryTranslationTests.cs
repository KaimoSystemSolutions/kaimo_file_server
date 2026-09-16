using Kaimo_File_Server.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Kaimo_File_Server.Tests;

/// <summary>
/// Settles the LIKE-escaping question for the REAL production provider (Npgsql),
/// without opening a connection.
///
/// Every share-relative subtree query in the repositories is expressed as
/// <c>m.Path == prefix || m.Path.StartsWith(prefix + "/")</c> where the prefix is a
/// captured variable, i.e. a query PARAMETER. If EF Core / Npgsql translate a
/// parameterized <see cref="string.StartsWith(string)"/> to a bare <c>LIKE @p || '%'</c>
/// with no <c>ESCAPE</c> clause, then a folder named <c>report_2024</c> silently
/// matches <c>reportX2024</c> (<c>_</c> is a single-character LIKE wildcard) and its
/// ACLs / versions are deleted with it.
///
/// This builds a context on the real Npgsql provider with a dummy connection string
/// and asserts on <see cref="RelationalQueryableExtensions.ToQueryString"/>. It compiles
/// the LINQ through the genuine Npgsql SQL generator but opens NO connection, so it
/// runs on CI with no Docker, no PostgreSQL and no network.
///
/// Outcome (verified): EF Core / Npgsql translate the parameterized StartsWith to
/// <c>Path LIKE @p</c> where the parameter VALUE is pre-escaped with a backslash
/// (<c>report\_2024/%</c>). PostgreSQL's LIKE uses backslash as its DEFAULT escape
/// character, so no explicit <c>ESCAPE</c> clause is emitted and the underscore is
/// matched literally. The path-prefix queries are therefore already safe and no
/// LIKE-escaping fix is required; this test locks that in against a regression to an
/// unescaped pattern.
/// </summary>
public class PathPrefixQueryTranslationTests
{
    private static ApplicationDbContext NewNpgsqlContext()
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            // Host is never contacted: ToQueryString() only needs the provider to
            // build SQL, it does not execute.
            .UseNpgsql("Host=unused;Database=unused;Username=unused;Password=unused")
            .Options;
        return new ApplicationDbContext(options);
    }

    [Fact]
    public void StartsWithPrefix_GeneratedNpgsqlSql_EscapesLikeMetacharacters()
    {
        using var db = NewNpgsqlContext();

        // Reproduce AclRepository.DeleteFileMetadataPathsAsync's shape exactly:
        // captured variables, so the arguments become query parameters.
        var shareId = Guid.NewGuid();
        var normalized = "report_2024";
        var prefix = normalized + "/";

        var sql = db.FileMetadata
            .Where(m => m.ShareId == shareId)
            .Where(m => m.Path == normalized || m.Path.StartsWith(prefix))
            .ToQueryString();

        var usesLike = sql.Contains("LIKE", StringComparison.OrdinalIgnoreCase);

        // Safe outcomes, any one of which holds:
        //  (a) no LIKE at all (e.g. a left()/substring comparison), or
        //  (b) the LIKE pattern escapes the metacharacter, either via an explicit
        //      ESCAPE clause or — as Npgsql does here — by pre-escaping the parameter
        //      value with backslash, PostgreSQL's default LIKE escape character.
        // The defect Phase 2 would fix is a LIKE against a RAW 'report_2024/%' pattern.
        var underscoreEscaped = sql.Contains(@"report\_2024", StringComparison.Ordinal);
        var usesExplicitEscape = sql.Contains("ESCAPE", StringComparison.OrdinalIgnoreCase);

        Assert.True(!usesLike || underscoreEscaped || usesExplicitEscape,
            "Parameterized StartsWith produced a LIKE against an unescaped pattern, so '_' "
            + "and '%' in a folder name act as wildcards. Generated SQL:\n" + sql);
    }
}

using System.Data.Common;
using Kaimo_File_Server.Core.Security;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace Kaimo_File_Server.Infrastructure.Persistence;

/// <summary>
/// Read-only demo guard for the database. Registered on the DbContext factory only
/// when <c>KAIMO_DEMO_READONLY=true</c>; it throws on any write that would actually
/// reach the store. Two paths are covered:
/// <list type="bullet">
/// <item>SaveChanges with pending changes (every tracked entity write and every
/// settings write through <see cref="Configuration.ConfigRepository"/>).</item>
/// <item>Non-query commands that bypass the change tracker — bulk
/// <c>ExecuteUpdate</c>/<c>ExecuteDelete</c> and raw SQL writes. Only plain
/// <c>SELECT</c> statements (e.g. advisory locks) may run through that path.</item>
/// </list>
/// A SaveChanges with nothing pending is left to proceed harmlessly, so incidental
/// no-op saves on read paths keep working. Reader/scalar commands are not inspected:
/// a raw write executed as a query (e.g. <c>UPDATE … RETURNING</c> via <c>SqlQuery</c>)
/// would pass, so such statements must not be introduced on demo-reachable paths.
/// </summary>
public sealed class ReadOnlyDemoSaveInterceptor : SaveChangesInterceptor, IDbCommandInterceptor
{
    public override InterceptionResult<int> SavingChanges(
        DbContextEventData eventData, InterceptionResult<int> result)
    {
        if (eventData.Context?.ChangeTracker.HasChanges() == true)
            throw new ReadOnlyDemoException();
        return result;
    }

    public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
        DbContextEventData eventData, InterceptionResult<int> result,
        CancellationToken cancellationToken = default)
    {
        if (eventData.Context?.ChangeTracker.HasChanges() == true)
            throw new ReadOnlyDemoException();
        return ValueTask.FromResult(result);
    }

    public InterceptionResult<int> NonQueryExecuting(
        DbCommand command, CommandEventData eventData, InterceptionResult<int> result)
    {
        if (IsWrite(command))
            throw new ReadOnlyDemoException();
        return result;
    }

    public ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(
        DbCommand command, CommandEventData eventData, InterceptionResult<int> result,
        CancellationToken cancellationToken = default)
    {
        if (IsWrite(command))
            throw new ReadOnlyDemoException();
        return ValueTask.FromResult(result);
    }

    // Allow-list: anything on the non-query path that is not a plain SELECT counts as
    // a write. Leading "--" tag lines (EF TagWith) are skipped first.
    private static bool IsWrite(DbCommand command)
    {
        var sql = command.CommandText.AsSpan().TrimStart();
        while (sql.StartsWith("--"))
        {
            int newline = sql.IndexOf('\n');
            if (newline < 0) return false;
            sql = sql[(newline + 1)..].TrimStart();
        }
        return !sql.StartsWith("SELECT", StringComparison.OrdinalIgnoreCase);
    }
}

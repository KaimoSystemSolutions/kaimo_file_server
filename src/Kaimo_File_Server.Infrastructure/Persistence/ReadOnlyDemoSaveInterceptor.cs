using Kaimo_File_Server.Core.Security;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace Kaimo_File_Server.Infrastructure.Persistence;

/// <summary>
/// Read-only demo guard for the database. Registered on the DbContext factory only
/// when <c>KAIMO_DEMO_READONLY=true</c>; it throws on any SaveChanges that would
/// actually persist a change. Because every entity write AND every settings write
/// (<see cref="Configuration.ConfigRepository"/>) funnels through SaveChanges, this
/// single interceptor blocks the entire DB + settings write surface — including code
/// that forgets its own permission check.
///
/// A SaveChanges with nothing pending is left to proceed harmlessly, so incidental
/// no-op saves on read paths keep working.
/// </summary>
public sealed class ReadOnlyDemoSaveInterceptor : SaveChangesInterceptor
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
}

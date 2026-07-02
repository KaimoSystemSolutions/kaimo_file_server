using Kaimo_File_Server.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace Kaimo_File_Server.Tests.Infrastructure;

/// <summary>
/// An <see cref="IDbContextFactory{TContext}"/> that hands out fresh
/// <see cref="ApplicationDbContext"/> instances, all bound to the SAME open
/// Sqlite connection.
///
/// Why a shared connection: an in-memory Sqlite database only lives as long as
/// at least one connection to it is open. By keeping one connection open for the
/// lifetime of the test and layering every context on top of it, all contexts see
/// the exact same database — writes committed through one context are immediately
/// visible to the next.
///
/// Why this matters for regression tests: production repositories that take an
/// <c>IDbContextFactory</c> create a brand-new context per operation. This factory
/// reproduces that behaviour faithfully, so a view model that saves through one
/// context and is then asserted through another proves the value truly round-tripped
/// to the store — not just to EF's in-memory change tracker.
/// </summary>
public sealed class TestDbContextFactory : IDbContextFactory<ApplicationDbContext>
{
    private readonly DbContextOptions<ApplicationDbContext> _options;

    public TestDbContextFactory(SqliteConnection connection)
    {
        _options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseSqlite(connection)
            .Options;
    }

    public ApplicationDbContext CreateDbContext() => new(_options);

    public Task<ApplicationDbContext> CreateDbContextAsync(CancellationToken ct = default)
        => Task.FromResult(CreateDbContext());
}

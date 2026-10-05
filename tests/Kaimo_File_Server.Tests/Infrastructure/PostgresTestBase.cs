using Kaimo_File_Server.Infrastructure;
using Kaimo_File_Server.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Npgsql;
using Xunit;

namespace Kaimo_File_Server.Tests.Infrastructure;

/// <summary>
/// A fact that only runs when <c>KAIMO_TEST_PG</c> holds a PostgreSQL connection string
/// (a user allowed to create databases), e.g.
/// <c>Host=localhost;Port=55432;Username=postgres;Password=test</c>. Without it the test is
/// reported as skipped, so a plain <c>dotnet test</c> stays green on any machine.
/// </summary>
public sealed class PostgresFactAttribute : FactAttribute
{
    public const string EnvironmentVariable = "KAIMO_TEST_PG";

    public PostgresFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(EnvironmentVariable)))
            Skip = $"Set {EnvironmentVariable} to a PostgreSQL connection string to run.";
    }
}

/// <summary>
/// Context factory configured exactly like production (Npgsql, retry-on-failure, connection
/// defaults), so tests observe the real execution strategy, isolation levels and locks that
/// Sqlite cannot reproduce.
/// </summary>
public sealed class PostgresDbContextFactory : IDbContextFactory<ApplicationDbContext>
{
    private readonly DbContextOptions<ApplicationDbContext> _options;

    public PostgresDbContextFactory(string connectionString, params IInterceptor[] interceptors)
    {
        var builder = new DbContextOptionsBuilder<ApplicationDbContext>();
        ServiceCollectionExtensions.UseKaimoNpgsql(builder, connectionString);
        _options = builder.AddInterceptors(interceptors).Options;
    }

    public ApplicationDbContext CreateDbContext() => new(_options);

    public Task<ApplicationDbContext> CreateDbContextAsync(CancellationToken ct = default)
        => Task.FromResult(CreateDbContext());
}

/// <summary>
/// Base class for tests that need real PostgreSQL semantics. Every test class instance gets
/// its own throw-away database with the production schema, dropped again afterwards.
/// Use together with <see cref="PostgresFactAttribute"/>.
/// </summary>
public abstract class PostgresTestBase : IAsyncLifetime
{
    private readonly string _serverConnectionString;
    private readonly string _database = "kaimo_test_" + Guid.NewGuid().ToString("N");

    /// <summary>Connection string of this test's database, with production defaults applied.</summary>
    protected string ConnectionString { get; }

    protected PostgresDbContextFactory DbFactory { get; }

    protected PostgresTestBase()
    {
        _serverConnectionString = Environment.GetEnvironmentVariable(PostgresFactAttribute.EnvironmentVariable)!;
        ConnectionString = ServiceCollectionExtensions.BuildConnectionString(
            new NpgsqlConnectionStringBuilder(_serverConnectionString) { Database = _database }.ConnectionString,
            "tests");
        DbFactory = new PostgresDbContextFactory(ConnectionString);
    }

    /// <summary>A factory on the same database whose contexts run the given interceptors.</summary>
    protected PostgresDbContextFactory FactoryWith(params IInterceptor[] interceptors)
        => new(ConnectionString, interceptors);

    protected ApplicationDbContext NewContext() => DbFactory.CreateDbContext();

    public async Task InitializeAsync()
    {
        await using var db = NewContext();
        await db.Database.EnsureCreatedAsync();
    }

    public async Task DisposeAsync()
    {
        NpgsqlConnection.ClearAllPools();
        await using var connection = new NpgsqlConnection(_serverConnectionString);
        await connection.OpenAsync();
        await using var drop = new NpgsqlCommand($"DROP DATABASE IF EXISTS \"{_database}\" WITH (FORCE)", connection);
        await drop.ExecuteNonQueryAsync();
    }
}

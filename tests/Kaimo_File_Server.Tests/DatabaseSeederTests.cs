using Kaimo_File_Server.Core.Domain.Identity;
using Kaimo_File_Server.Infrastructure;
using Kaimo_File_Server.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Kaimo_File_Server.Tests;

/// <summary>
/// Regression tests for <see cref="DatabaseSeeder"/> credential safety.
///
/// The historical bugs this locks down:
///   • A built-in "guest" account that was ENABLED with an empty password
///     (anyone could authenticate, notably over SMB/NTLM).
///   • Hard-coded demo credentials (admin/admin1234, …) seeded into every
///     environment, including production.
///
/// Uses Sqlite in-memory (relational → supports the TPC identity hierarchy).
/// </summary>
public class DatabaseSeederTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly ApplicationDbContext _db;
    private readonly PasswordService _passwords = new();

    public DatabaseSeederTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();

        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseSqlite(_connection)
            .Options;

        _db = new ApplicationDbContext(options);
        _db.Database.EnsureCreated();
    }

    public void Dispose()
    {
        _db.Dispose();
        _connection.Dispose();
    }

    private async Task SeedAsync(params (string Key, string Value)[] config)
    {
        var cfg = new ConfigurationBuilder()
            .AddInMemoryCollection(config.ToDictionary(c => c.Key, c => (string?)c.Value))
            .Build();

        var seeder = new DatabaseSeeder(
            _db, _passwords, NullLogger<DatabaseSeeder>.Instance, cfg);

        await seeder.SeedAsync();
    }

    // ─────────────── The core invariant ───────────────

    [Theory]
    [InlineData("true")]   // demo mode
    [InlineData("false")]  // production / bootstrap mode
    public async Task SeedAsync_NoEnabledUserHasEmptyPassword(string demo)
    {
        await SeedAsync(("Seed:DemoData", demo));

        var enabled = await _db.Users.Where(u => u.IsEnabled).ToListAsync();
        Assert.NotEmpty(enabled); // there must always be *some* way in

        foreach (var user in enabled)
        {
            Assert.False(
                _passwords.VerifyPassword("", user.PasswordHash),
                $"Enabled user '{user.Username}' accepts an empty password.");
        }
    }

    // ─────────────── Production / bootstrap mode ───────────────

    [Fact]
    public async Task SeedAsync_WithoutDemoData_DoesNotSeedDemoCredentials()
    {
        await SeedAsync(("Seed:DemoData", "false"));

        var users = await _db.Users.ToListAsync();

        // No account may carry a well-known demo password.
        foreach (var user in users)
        {
            Assert.False(_passwords.VerifyPassword("admin1234", user.PasswordHash),
                $"User '{user.Username}' still uses the demo password 'admin1234'.");
            Assert.False(_passwords.VerifyPassword("1234", user.PasswordHash),
                $"User '{user.Username}' still uses the demo password '1234'.");
        }

        // The demo-only "guest" account must not be created at all here.
        Assert.DoesNotContain(users, u => u.Username == "guest");
    }

    [Fact]
    public async Task SeedAsync_WithoutDemoData_CreatesEnabledBootstrapAdmin()
    {
        await SeedAsync(("Seed:DemoData", "false"));

        var admin = await _db.Users.SingleOrDefaultAsync(u => u.Username == "admin");
        Assert.NotNull(admin);
        Assert.True(admin!.IsEnabled);
    }

    [Fact]
    public async Task SeedAsync_WithConfiguredAdminPassword_UsesIt()
    {
        const string pw = "Configured#Bootstrap_Pw_42";
        await SeedAsync(("Seed:DemoData", "false"), ("Seed:AdminPassword", pw));

        var admin = await _db.Users.SingleAsync(u => u.Username == "admin");
        Assert.True(_passwords.VerifyPassword(pw, admin.PasswordHash));
    }

    // ─────────────── Demo mode ───────────────

    [Fact]
    public async Task SeedAsync_DemoData_SeedsDemoUsers()
    {
        await SeedAsync(("Seed:DemoData", "true"));

        var admin = await _db.Users.SingleAsync(u => u.Username == "admin");
        Assert.True(_passwords.VerifyPassword("admin1234", admin.PasswordHash));
    }

    [Fact]
    public async Task SeedAsync_DemoData_DoesNotSeedGuest()
    {
        await SeedAsync(("Seed:DemoData", "true"));

        // There is no guest/anonymous account in any mode — only real, password-protected users.
        Assert.False(await _db.Users.AnyAsync(u => u.Username == "guest"));
    }

    // ─────────────── Idempotency ───────────────

    [Fact]
    public async Task SeedAsync_RunTwice_DoesNotDuplicate()
    {
        await SeedAsync(("Seed:DemoData", "true"));
        await SeedAsync(("Seed:DemoData", "true"));

        Assert.Equal(1, await _db.Users.CountAsync(u => u.Username == "admin"));
        Assert.False(await _db.Users.AnyAsync(u => u.Username == "guest"));
    }
}

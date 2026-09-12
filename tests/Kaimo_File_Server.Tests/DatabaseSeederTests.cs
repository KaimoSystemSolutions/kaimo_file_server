using Kaimo_File_Server.Core.Domain.Identity;
using Kaimo_File_Server.Core.Helpers;
using Kaimo_File_Server.Infrastructure;
using Kaimo_File_Server.Infrastructure.Persistence;
using Kaimo_File_Server.Infrastructure.Security;
using Kaimo_File_Server.Tests.Infrastructure;
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
    private readonly TestDbContextFactory _dbFactory;
    private readonly ApplicationDbContext _db;
    private readonly PasswordService _passwords = new();
    private readonly AesGcmNtHashProtector _ntHashProtector =
        new(new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
                { ["NtHash:EncryptionKey"] = "unit-test-nt-hash-key" })
            .Build());

    public DatabaseSeederTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();

        _dbFactory = new TestDbContextFactory(_connection);
        _db = _dbFactory.CreateDbContext();
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
            _dbFactory, _passwords, _ntHashProtector, NullLogger<DatabaseSeeder>.Instance, cfg);

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
        Assert.Contains(await _db.Departments.Select(d => d.Name).ToListAsync(),
            name => name == "Entwicklung");
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

    // ─────────────── System groups (well-known IDs) ───────────────

    [Fact]
    public async Task SeedAsync_CreatesSystemGroupsWithWellKnownIds()
    {
        await SeedAsync(("Seed:DemoData", "false"));

        using var db = _dbFactory.CreateDbContext();
        Assert.True(await db.Groups.AnyAsync(g => g.Id == WellKnownGUIDs.GROUP_ADMINS && g.Name == "Admins"));
        Assert.True(await db.Groups.AnyAsync(g => g.Id == WellKnownGUIDs.GROUP_EVERYONE && g.Name == "Everyone"));
    }

    [Fact]
    public async Task SeedAsync_BootstrapAdmin_IsInAdminsGroupWithAdminRole()
    {
        await SeedAsync(("Seed:DemoData", "false"));

        using var db = _dbFactory.CreateDbContext();
        var admin = await db.Users.SingleAsync(u => u.Username == "admin");
        Assert.True(await db.UserGroups.AnyAsync(ug => ug.UserId == admin.Id && ug.GroupId == WellKnownGUIDs.GROUP_ADMINS));
        Assert.True(await db.ScopedRoleAssignments.AnyAsync(a => a.PrincipalId == admin.Id && a.RoleId == WellKnownGUIDs.ROLE_ADMIN));
    }

    [Fact]
    public async Task SeedAsync_AllUsersAreMembersOfEveryone()
    {
        await SeedAsync(("Seed:DemoData", "true"));

        using var db = _dbFactory.CreateDbContext();
        var userIds = await db.Users.Select(u => u.Id).ToListAsync();
        Assert.NotEmpty(userIds);
        foreach (var id in userIds)
            Assert.True(await db.UserGroups.AnyAsync(ug => ug.UserId == id && ug.GroupId == WellKnownGUIDs.GROUP_EVERYONE),
                $"User {id} is not a member of the Everyone group.");
    }

    [Fact]
    public async Task SeedAsync_RemapsLegacySystemGroupOntoWellKnownId()
    {
        // Simulate a pre-well-known-GUID install: an "Admins" group with a random id
        // plus a membership row pointing at it.
        var legacyId = Guid.NewGuid();
        var user = new User(
            Guid.NewGuid(), "Legacy", "legacy",
            _passwords.HashPassword("Passw0rd!"), "nt-hash",
            description: "", email: "", isEnabled: true, canChangePassword: true);
        _db.Groups.Add(new Group(legacyId, "Admins"));
        _db.Users.Add(user);
        _db.UserGroups.Add(new UserGroup(user.Id, legacyId));
        await _db.SaveChangesAsync();

        await SeedAsync(("Seed:DemoData", "false"));

        using var db = _dbFactory.CreateDbContext();
        Assert.False(await db.Groups.AnyAsync(g => g.Id == legacyId));                         // old row gone
        Assert.True(await db.Groups.AnyAsync(g => g.Id == WellKnownGUIDs.GROUP_ADMINS));       // remapped
        Assert.True(await db.UserGroups.AnyAsync(                                              // membership repointed
            ug => ug.UserId == user.Id && ug.GroupId == WellKnownGUIDs.GROUP_ADMINS));
    }
}

using Kaimo_File_Server.Core.Security;
using Kaimo_File_Server.Infrastructure.Security;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace Kaimo_File_Server.Infrastructure.Persistence;

/// <summary>
/// Seeds the database with system roles, default groups, departments,
/// and (on first run) test users with realistic scoped assignments.
///
/// Safe to call on every startup — all operations are idempotent.
///
/// Execution order:
///   1. Clean up duplicates (migration safety net)
///   2. Seed Global department (must exist before Groups/Shares)
///   3. Seed system roles with ManagementPermission flags
///   4. Seed default groups (with DepartmentId = Global)
///   5. Seed department hierarchy with default file permissions
///   6. Seed test users + assignments (first run only)
/// </summary>
public partial class DatabaseSeeder
{
    private readonly IDbContextFactory<ApplicationDbContext> _dbFactory;
    private readonly IPasswordService _passwordService;
    private readonly INtHashProtector _ntHashProtector;
    private readonly ILogger<DatabaseSeeder> _logger;
    private readonly IConfiguration _configuration;

    public DatabaseSeeder(
        IDbContextFactory<ApplicationDbContext> db,
        IPasswordService passwordService,
        INtHashProtector ntHashProtector,
        ILogger<DatabaseSeeder> logger,
        IConfiguration configuration)
    {
        _dbFactory = db;
        _passwordService = passwordService;
        _ntHashProtector = ntHashProtector;
        _logger = logger;
        _configuration = configuration;
    }

    /// <summary>
    /// Whether to seed the demo/test users (admin1234, marco/1234, …). These
    /// are convenient for local development but are a security liability in
    /// production, so they are OFF unless explicitly enabled via
    /// <c>Seed:DemoData=true</c> (set in appsettings.Development.json).
    /// </summary>
    private bool SeedDemoData => _configuration.GetValue("Seed:DemoData", false);

    // ══════════════════════════════════════════
    //  Main Entry Point
    // ══════════════════════════════════════════

    public async Task SeedAsync()
    {
        await CleanupDuplicatesAsync();
        await SeedGlobalDepartmentAsync();
        await NormalizeGlobalDepartmentAsync();
        await SeedSystemGroupsAsync();
        await SeedSystemRolesAsync();
        await RetireUserRoleAsync();

        if (SeedDemoData)
        {
            await SeedDepartmentsAsync();
            await SeedGroupsAsync();
            await SeedTestUsersAsync();
        }
        else
        {
            await SeedBootstrapAdminAsync();
        }

        // Runs after all users exist, so it covers seeded, bootstrapped and pre-existing users.
        await BackfillEveryoneMembershipAsync();

        await SeedConfigAsync();

        // After SeedConfigAsync, which only seeds defaults into an empty config table.
        await using (var db = await _dbFactory.CreateDbContextAsync())
            await NtHashKeyCanary.EnsureAsync(db, _ntHashProtector, _logger);
    }

}

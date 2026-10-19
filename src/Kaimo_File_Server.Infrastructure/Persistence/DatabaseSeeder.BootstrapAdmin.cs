using Kaimo_File_Server.Core.Domain.Identity;
using Kaimo_File_Server.Core.Helpers;
using Kaimo_File_Server.Core.Logging;
using Kaimo_File_Server.Core.Security;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using System.Security.Cryptography;

namespace Kaimo_File_Server.Infrastructure.Persistence;

public partial class DatabaseSeeder
{
    // ══════════════════════════════════════════
    //  Bootstrap admin (production / non-demo)
    // ══════════════════════════════════════════

    /// <summary>
    /// Ensures a single administrator account exists so a fresh production
    /// system is reachable — WITHOUT shipping a publicly known password.
    ///
    /// The password is taken from <c>Seed:AdminPassword</c> (env/secret) if
    /// present; otherwise a strong random one is generated and logged exactly
    /// once at startup. There is intentionally no hard-coded fallback.
    /// </summary>
    private async Task SeedBootstrapAdminAsync()
    {
        await using var db = await _dbFactory.CreateDbContextAsync();

        if (await db.Users.AnyAsync())
        {
            _logger.LogDebug(LogEvents.SeedBootstrapAdminSkipped, LogMessages.SeedBootstrapAdminSkipped);
            return;
        }

        var roles = await LoadRoleLookupAsync();
        if (!roles.TryGetValue("Administrator", out var adminRole))
        {
            _logger.LogWarning(LogEvents.SeedAdminRoleMissing, LogMessages.SeedAdminRoleMissing);
            return;
        }

        string? configuredPassword = _configuration["Seed:AdminPassword"];

        var generated = string.IsNullOrWhiteSpace(configuredPassword);
        var password = generated ? GenerateStrongPassword() : configuredPassword!;

        var admin = MakeUser(
            "Administrator", "admin", password,
            "Built-in administrator account", "admin@kaimo.local");
        // The initial password sits in the logs or in the deployment's .env/compose file,
        // so the administrator must replace it after the first sign-in.
        admin.MustChangePassword = true;

        db.Users.Add(admin);
        await db.SaveChangesAsync();

        db.ScopedRoleAssignments.Add(
            ScopedRoleAssignment.Global(admin.Id, adminRole.Id));
        // Mirror the admin role into the Admins group (the coupling invariant the runtime
        // enforces). Everyone membership is handled by the backfill at the end of seeding.
        db.UserGroups.Add(new UserGroup(admin.Id, WellKnownGUIDs.GROUP_ADMINS));
        await db.SaveChangesAsync();

        if (generated)
        {
            _logger.LogWarning(LogEvents.SeedBootstrapAdminPassword, LogMessages.SeedBootstrapAdminPassword, password);
        }
        else
        {
            _logger.LogInformation(LogEvents.SeedBootstrapAdminCreated, LogMessages.SeedBootstrapAdminCreated);
        }
    }

    /// <summary>
    /// Cryptographically strong, URL-safe random password (~24 chars).
    /// </summary>
    private static string GenerateStrongPassword()
    {
        Span<byte> bytes = stackalloc byte[18];
        RandomNumberGenerator.Fill(bytes);
        return Convert.ToBase64String(bytes)
            .Replace('+', 'A').Replace('/', 'B').Replace('=', 'C');
    }
}

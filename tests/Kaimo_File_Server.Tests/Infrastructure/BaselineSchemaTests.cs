using Kaimo_File_Server.Core.Domain;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Kaimo_File_Server.Tests.Infrastructure;

/// <summary>
/// The 1.0 release starts from a single baseline migration. These tests pin what that
/// baseline must carry on its own and that a pre-1.0 beta database is refused clearly.
/// </summary>
public sealed class BaselineSchemaTests : DatabaseTestBase
{
    [Fact]
    public async Task FreshSchema_ContainsTheMicrosoftPublicProviderProfile()
    {
        // New OneDrive connections reference this row through a foreign key.
        await using var db = NewContext();
        var profile = await db.ProviderProfiles.AsNoTracking()
            .SingleAsync(p => p.Id == WellKnownProviderProfiles.MicrosoftPublicClient);

        Assert.Equal("onedrive", profile.ProviderId);
        Assert.Equal("common", profile.TenantOrOrganizationId);
        Assert.Equal("e966f5be-e8a1-4c67-b322-aac34c1ab642", profile.PublicClientId);
        Assert.Equal("offline_access Files.ReadWrite User.Read", profile.AllowedScopes);
        Assert.True(profile.Enabled);
    }

    [Fact]
    public async Task UnknownMigrationGuard_RejectsABetaDatabase()
    {
        await using var db = NewContext();
        await db.Database.ExecuteSqlRawAsync(
            "CREATE TABLE \"__EFMigrationsHistory\" (\"MigrationId\" TEXT NOT NULL PRIMARY KEY, \"ProductVersion\" TEXT NOT NULL)");
        await db.Database.ExecuteSqlRawAsync(
            "INSERT INTO \"__EFMigrationsHistory\" VALUES ('20261101130014_AddUserSecondFactor', '10.0.0')");

        await Assert.ThrowsAsync<Kaimo_File_Server.Infrastructure.SchemaVersionMismatchException>(
            () => Kaimo_File_Server.Infrastructure.ServiceCollectionExtensions.ThrowIfUnknownMigrationsAsync(db));
    }

    [Fact]
    public async Task UnknownMigrationGuard_AcceptsAFreshDatabase()
    {
        await using var db = NewContext();
        await Kaimo_File_Server.Infrastructure.ServiceCollectionExtensions.ThrowIfUnknownMigrationsAsync(db);
    }
}

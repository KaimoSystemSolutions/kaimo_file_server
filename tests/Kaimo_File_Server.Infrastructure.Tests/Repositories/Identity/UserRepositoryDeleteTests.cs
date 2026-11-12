using System.Text.Json;
using Kaimo_File_Server.Core.Domain;
using Kaimo_File_Server.Core.Domain.Notifications;
using Kaimo_File_Server.Core.Security;
using Kaimo_File_Server.Core.Security.Acl;
using Kaimo_File_Server.Core.Security.Authentication;
using Kaimo_File_Server.Infrastructure.Configuration;
using Kaimo_File_Server.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Kaimo_File_Server.Infrastructure.Tests.Repositories.Identity;

/// <summary>
/// Deleting a user removes everything that only existed for that account and can never be
/// used again (ACL entries, public links, recipient/policy list entries) and detaches the
/// references that must outlive it (sync definitions), while leaving other principals untouched.
/// </summary>
public sealed class UserRepositoryDeleteTests : DatabaseTestBase
{
    [Fact]
    public async Task DeleteAsync_RemovesAclEntriesOfUser_KeepsOthers()
    {
        var gone = SeedUser("gone");
        var stays = SeedUser("stays");
        var share = SeedShare("acl");
        var file = SeedFileWithAcl(share.Id, "docs", isDirectory: true, ownerId: stays.Id,
            new AccessEntry(gone.Id, AclEntryType.Allow, FilePermission.ReadAll, AclInheritance.ThisFolder),
            new AccessEntry(stays.Id, AclEntryType.Allow, FilePermission.ReadAll, AclInheritance.ThisFolder));

        await UserRepo().DeleteAsync(gone.Id);

        await using var db = NewContext();
        var entry = Assert.Single(await db.AccessEntries.Where(e => e.FileMetadataId == file.Id).ToListAsync());
        Assert.Equal(stays.Id, entry.PrincipalId);
    }

    [Fact]
    public async Task DeleteAsync_RemovesShareLinksCreatedByUser()
    {
        var gone = SeedUser("gone");
        var stays = SeedUser("stays");
        await using (var db = NewContext())
        {
            db.ShareLinks.Add(Link(gone.Id, "a"));
            db.ShareLinks.Add(Link(stays.Id, "b"));
            await db.SaveChangesAsync();
        }

        await UserRepo().DeleteAsync(gone.Id);

        await using var check = NewContext();
        var link = Assert.Single(await check.ShareLinks.ToListAsync());
        Assert.Equal(stays.Id, link.CreatedByUserId);
    }

    [Fact]
    public async Task DeleteAsync_UserReferencedBySyncDefinition_SucceedsAndDetachesReferences()
    {
        var gone = SeedUser("gone");
        var share = SeedShare("sync");
        var connection = new StorageConnection
        {
            CreatedByUserId = gone.Id, ProviderId = "onedrive", Name = "c",
            State = StorageConnectionState.Ready, ConcurrencyVersion = 1,
        };
        var definition = new SyncDefinition
        {
            ConnectionId = connection.Id, LocalShareId = share.Id, LocalPath = "x",
            RunAsUserId = gone.Id, CreatedByUserId = gone.Id,
        };
        await using (var db = NewContext())
        {
            db.StorageConnections.Add(connection);
            db.SyncDefinitions.Add(definition);
            await db.SaveChangesAsync();
        }

        await UserRepo().DeleteAsync(gone.Id);

        await using var check = NewContext();
        Assert.False(await check.Users.AnyAsync(u => u.Id == gone.Id));
        var persisted = await check.SyncDefinitions.SingleAsync();
        Assert.Null(persisted.RunAsUserId);
        Assert.Null(persisted.CreatedByUserId);
    }

    [Fact]
    public async Task DeleteAsync_PrunesUserFromMailRuleRecipients()
    {
        var gone = SeedUser("gone");
        var stays = SeedUser("stays");
        var rule = new MailRule { EventType = "x", Name = "r", Enabled = true };
        rule.SetRecipients([
            new RecipientSpec(RecipientKind.User, gone.Id.ToString()),
            new RecipientSpec(RecipientKind.User, stays.Id.ToString()),
            new RecipientSpec(RecipientKind.ContextRole, "affected"),
        ]);
        await using (var db = NewContext())
        {
            db.MailRules.Add(rule);
            await db.SaveChangesAsync();
        }

        await UserRepo().DeleteAsync(gone.Id);

        await using var check = NewContext();
        var recipients = (await check.MailRules.SingleAsync()).GetRecipients();
        Assert.Equal(2, recipients.Count);
        Assert.DoesNotContain(recipients, r => r.Value == gone.Id.ToString());
    }

    [Fact]
    public async Task DeleteAsync_PrunesUserFromSecondFactorPolicyAndAccessRules()
    {
        var gone = SeedUser("gone");
        var stays = SeedUser("stays");
        var policy = new SecondFactorPolicy
        {
            Mode = SecondFactorMode.Required, UserIds = [gone.Id, stays.Id], Issuer = "Kept",
        };
        var restrictions = new AccessRestrictionSettings
        {
            Rules = [new AccessRule { Name = "office", UserIds = [gone.Id, stays.Id], Networks = ["10.0.0.0/8"] }],
            SmbAllowedNetworks = ["10.0.0.0/8"],
        };
        await using (var db = NewContext())
        {
            db.ConfigSettings.Add(Setting(SecondFactorPolicy.ConfigKey, policy));
            db.ConfigSettings.Add(Setting(AccessRestrictionSettings.ConfigKey, restrictions));
            await db.SaveChangesAsync();
        }

        await UserRepo().DeleteAsync(gone.Id);

        await using var check = NewContext();
        var savedPolicy = Read<SecondFactorPolicy>(check, SecondFactorPolicy.ConfigKey);
        Assert.Equal([stays.Id], savedPolicy.UserIds);
        Assert.Equal("Kept", savedPolicy.Issuer);
        Assert.Equal(SecondFactorMode.Required, savedPolicy.Mode);

        var savedRestrictions = Read<AccessRestrictionSettings>(check, AccessRestrictionSettings.ConfigKey);
        var rule = Assert.Single(savedRestrictions.Rules);
        Assert.Equal([stays.Id], rule.UserIds);
        Assert.Equal(["10.0.0.0/8"], rule.Networks);
        Assert.Equal(["10.0.0.0/8"], savedRestrictions.SmbAllowedNetworks);
    }

    [Fact]
    public async Task DeleteAsync_WithoutPolicySettings_DoesNotCreateThem()
    {
        var gone = SeedUser("gone");

        await UserRepo().DeleteAsync(gone.Id);

        await using var check = NewContext();
        Assert.False(await check.ConfigSettings.AnyAsync());
    }

    private static ShareLink Link(Guid creator, string name) => new()
    {
        TokenHash = ShareLink.HashToken(name), ShareId = Guid.NewGuid(),
        RootRelativePath = name, DisplayName = name, CreatedByUserId = creator,
    };

    private static ConfigSetting Setting<T>(string key, T value)
        => new() { Key = key, Value = JsonSerializer.Serialize(value), UpdatedAt = DateTime.UtcNow };

    private static T Read<T>(ApplicationDbContext db, string key)
        => JsonSerializer.Deserialize<T>(db.ConfigSettings.Single(s => s.Key == key).Value)!;
}

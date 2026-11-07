using Kaimo_File_Server.Core.Domain;
using Kaimo_File_Server.Core.Domain.Identity;
using Kaimo_File_Server.Core.Helpers;
using Kaimo_File_Server.Core.Security;
using Xunit;

namespace Kaimo_File_Server.Core.Tests.Security;

/// <summary>
/// Folder-depth precedence of ACL entries, evaluated by the real <see cref="AclService"/>
/// over the real database (NTFS-like): the deepest folder level that has any entry for one
/// of the user's principals covering the requested permission decides; within that level
/// Deny wins over Allow.
/// </summary>
public class AclDepthPrecedenceDatabaseTests : DatabaseTestBase
{
    private static AccessEntry Allow(Guid principal, FilePermission p = FilePermission.FullControl)
        => new(principal, AclEntryType.Allow, p, AclInheritance.Everything);

    private static AccessEntry Deny(Guid principal, FilePermission p = FilePermission.ListReadData)
        => new(principal, AclEntryType.Deny, p, AclInheritance.Everything);

    [Fact]
    public async Task DeeperAllow_SamePrincipal_OverridesAncestorDeny()
    {
        var user = SeedUser("alice");
        var share = SeedShare("docs");
        SeedFileWithAcl(share.Id, "docs", true, user.Id, Deny(user.Id));
        SeedFileWithAcl(share.Id, "docs/open", true, user.Id, Allow(user.Id));

        var acl = BuildAclService();
        var ctx = ContextFor(user);

        Assert.False(await acl.HasAccessAsync(ctx, share.Id, "docs", true, FilePermission.ListReadData));
        Assert.False(await acl.HasAccessAsync(ctx, share.Id, "docs/other.txt", false, FilePermission.ListReadData));
        Assert.True(await acl.HasAccessAsync(ctx, share.Id, "docs/open", true, FilePermission.ListReadData));
        Assert.True(await acl.HasAccessAsync(ctx, share.Id, "docs/open/deep/file.txt", false, FilePermission.ListReadData));
    }

    [Fact]
    public async Task DeeperAllow_SkippingLevels_OverridesGrandparentDeny()
    {
        var user = SeedUser("alice");
        var share = SeedShare("docs");
        SeedFileWithAcl(share.Id, "a", true, user.Id, Deny(user.Id));
        SeedFileWithAcl(share.Id, "a/b/c", true, user.Id, Allow(user.Id));

        var acl = BuildAclService();
        var ctx = ContextFor(user);

        Assert.False(await acl.HasAccessAsync(ctx, share.Id, "a/b/x.txt", false, FilePermission.ListReadData));
        Assert.True(await acl.HasAccessAsync(ctx, share.Id, "a/b/c/d.txt", false, FilePermission.ListReadData));
    }

    [Fact]
    public async Task DeeperDeny_SamePrincipal_OverridesAncestorAllow()
    {
        var user = SeedUser("alice");
        var share = SeedShare("docs");
        SeedFileWithAcl(share.Id, "docs", true, user.Id, Allow(user.Id));
        SeedFileWithAcl(share.Id, "docs/secret", true, user.Id, Deny(user.Id));

        var acl = BuildAclService();
        var ctx = ContextFor(user);

        Assert.True(await acl.HasAccessAsync(ctx, share.Id, "docs/public.txt", false, FilePermission.ListReadData));
        Assert.False(await acl.HasAccessAsync(ctx, share.Id, "docs/secret/x.txt", false, FilePermission.ListReadData));
    }

    [Fact]
    public async Task DeeperAllow_ForOtherPermission_DoesNotLiftAncestorDeny()
    {
        var user = SeedUser("alice");
        var share = SeedShare("docs");
        SeedFileWithAcl(share.Id, "docs", true, user.Id,
            Allow(user.Id), Deny(user.Id, FilePermission.CreateWriteData));
        SeedFileWithAcl(share.Id, "docs/sub", true, user.Id, Allow(user.Id, FilePermission.ListReadData));

        var acl = BuildAclService();
        var ctx = ContextFor(user);

        Assert.True(await acl.HasAccessAsync(ctx, share.Id, "docs/sub/f.txt", false, FilePermission.ListReadData));
        Assert.False(await acl.HasAccessAsync(ctx, share.Id, "docs/sub/f.txt", false, FilePermission.CreateWriteData));
    }

    [Fact]
    public async Task DeeperAllow_OnDifferentPrincipal_OverridesAncestorDeny()
    {
        var user = SeedUser("alice");
        var group = new Group(Guid.NewGuid(), "Team");
        using (var db = NewContext()) { db.Groups.Add(group); db.SaveChanges(); }
        var share = SeedShare("docs");
        SeedFileWithAcl(share.Id, "docs", true, user.Id, Deny(user.Id));
        SeedFileWithAcl(share.Id, "docs/team", true, user.Id, Allow(group.Id));

        var acl = BuildAclService();
        var ctx = new UserContext(user, [group], [], []);

        Assert.False(await acl.HasAccessAsync(ctx, share.Id, "docs/other.txt", false, FilePermission.ListReadData));
        Assert.True(await acl.HasAccessAsync(ctx, share.Id, "docs/team/f.txt", false, FilePermission.ListReadData));
    }

    [Fact]
    public async Task DeeperDeny_OnDifferentPrincipal_OverridesAncestorAllow()
    {
        var user = SeedUser("alice");
        var group = new Group(Guid.NewGuid(), "Team");
        using (var db = NewContext()) { db.Groups.Add(group); db.SaveChanges(); }
        var share = SeedShare("docs");
        SeedFileWithAcl(share.Id, "docs", true, user.Id, Allow(user.Id));
        SeedFileWithAcl(share.Id, "docs/locked", true, user.Id, Deny(group.Id));

        var acl = BuildAclService();
        var ctx = new UserContext(user, [group], [], []);

        Assert.False(await acl.HasAccessAsync(ctx, share.Id, "docs/locked/f.txt", false, FilePermission.ListReadData));
    }

    [Fact]
    public async Task SameLevel_DenyOnGroup_BeatsAllowOnUser()
    {
        var user = SeedUser("alice");
        var group = new Group(Guid.NewGuid(), "Team");
        using (var db = NewContext()) { db.Groups.Add(group); db.SaveChanges(); }
        var share = SeedShare("docs");
        SeedFileWithAcl(share.Id, "docs/sub", true, user.Id, Allow(user.Id), Deny(group.Id));

        var acl = BuildAclService();
        var ctx = new UserContext(user, [group], [], []);

        Assert.False(await acl.HasAccessAsync(ctx, share.Id, "docs/sub/f.txt", false, FilePermission.ListReadData));
    }

    [Fact]
    public async Task SameLevel_DenyStillBeatsAllow()
    {
        var user = SeedUser("alice");
        var share = SeedShare("docs");
        SeedFileWithAcl(share.Id, "docs", true, user.Id, Deny(user.Id));
        SeedFileWithAcl(share.Id, "docs/sub", true, user.Id, Allow(user.Id), Deny(user.Id));

        var acl = BuildAclService();

        Assert.False(await acl.HasAccessAsync(ContextFor(user), share.Id, "docs/sub/f.txt", false, FilePermission.ListReadData));
    }

    [Fact]
    public async Task Batch_AppliesSameDepthPrecedence()
    {
        var user = SeedUser("alice");
        var share = SeedShare("docs");
        SeedFileWithAcl(share.Id, "docs", true, user.Id, Deny(user.Id));
        SeedFileWithAcl(share.Id, "docs/open", true, user.Id, Allow(user.Id));

        var result = await BuildAclService().HasAccessBatchAsync(
            ContextFor(user), share.Id,
            [("docs/open/a.txt", false), ("docs/b.txt", false)],
            FilePermission.ListReadData);

        Assert.True(result[ShareRelativePath.Normalize("docs/open/a.txt")]);
        Assert.False(result[ShareRelativePath.Normalize("docs/b.txt")]);
    }
}

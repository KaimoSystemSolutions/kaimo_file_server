using Kaimo_File_Server.Core.Domain;
using Kaimo_File_Server.Core.Domain.Identity;
using Kaimo_File_Server.Core.Helpers;
using Kaimo_File_Server.Core.Security;
using Kaimo_File_Server.Core.Services;
using Kaimo_File_Server.Tests.Infrastructure;
using Kaimo_File_Server.Web.Components.ViewModels;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Kaimo_File_Server.Tests;

/// <summary>
/// End-to-end permission-lifecycle regression tests: they mutate ACLs (grant / revoke /
/// deny) and then ask the REAL <see cref="AclService"/> — over the real database — whether
/// access actually flipped. Unlike the view-model suites (which mock authorization), these
/// prove the whole chain: an ACL row is written/removed AND the access decision that reads
/// it back changes accordingly.
///
/// This is the scenario that most directly protects against a security regression:
/// "we removed the permission, but did access really get denied?"
/// </summary>
public class AclPermissionLifecycleDatabaseTests : DatabaseTestBase
{
    // ══════════════════ Repository/service level ══════════════════

    [Fact]
    public async Task RevokingAllow_DeniesPreviouslyGrantedAccess()
    {
        var user = SeedUser("alice");
        var share = SeedShare("docs");
        var ctx = ContextFor(user);
        var allow = new AccessEntry(user.Id, AclEntryType.Allow, FilePermission.ReadAll, AclInheritance.Everything);
        SeedFileWithAcl(share.Id, "docs", isDirectory: true, user.Id, allow);

        var acl = BuildAclService();

        // Granted → access is real.
        Assert.True(await acl.HasAccessAsync(ctx, share.Id, "docs", true, FilePermission.ListReadData));

        // Revoke the entry, then re-check through a fresh evaluation.
        await AclRepo().DeleteAsync(allow.Id);

        Assert.False(await acl.HasAccessAsync(ctx, share.Id, "docs", true, FilePermission.ListReadData));
    }

    [Fact]
    public async Task AddingDeny_OverridesExistingAllow_ThenRemovingDeny_RestoresAccess()
    {
        var user = SeedUser("alice");
        var share = SeedShare("docs");
        var ctx = ContextFor(user);
        var allow = new AccessEntry(user.Id, AclEntryType.Allow, FilePermission.FullControl, AclInheritance.Everything);
        var deny = new AccessEntry(user.Id, AclEntryType.Deny, FilePermission.ListReadData, AclInheritance.Everything);
        SeedFileWithAcl(share.Id, "docs", true, user.Id, allow, deny);

        var acl = BuildAclService();

        // Deny wins over Allow → no read access.
        Assert.False(await acl.HasAccessAsync(ctx, share.Id, "docs", true, FilePermission.ListReadData));

        // Remove the Deny → the surviving FullControl Allow grants access again.
        await AclRepo().DeleteAsync(deny.Id);

        Assert.True(await acl.HasAccessAsync(ctx, share.Id, "docs", true, FilePermission.ListReadData));
    }

    [Fact]
    public async Task RevokingGroupAllow_RemovesAccessForGroupMember()
    {
        var user = SeedUser("alice");
        var group = new Core.Domain.Identity.Group(Guid.NewGuid(), "Team");
        using (var db = NewContext()) { db.Groups.Add(group); db.SaveChanges(); }

        var share = SeedShare("docs");
        // Access is granted to the GROUP, and the user is a member of it.
        var ctx = new UserContext(user, [group], [], []);
        var groupAllow = new AccessEntry(group.Id, AclEntryType.Allow, FilePermission.ReadAll, AclInheritance.Everything);
        SeedFileWithAcl(share.Id, "docs", true, user.Id, groupAllow);

        var acl = BuildAclService();
        Assert.True(await acl.HasAccessAsync(ctx, share.Id, "docs", true, FilePermission.ListReadData));

        await AclRepo().DeleteAsync(groupAllow.Id);

        Assert.False(await acl.HasAccessAsync(ctx, share.Id, "docs", true, FilePermission.ListReadData));
    }

    [Fact]
    public async Task RevokingWriteEntry_LeavesReadIntact()
    {
        var user = SeedUser("alice");
        var share = SeedShare("docs");
        var ctx = ContextFor(user);
        var readAllow = new AccessEntry(user.Id, AclEntryType.Allow, FilePermission.ReadAll, AclInheritance.Everything);
        var writeAllow = new AccessEntry(user.Id, AclEntryType.Allow, FilePermission.CreateWriteData, AclInheritance.Everything);
        SeedFileWithAcl(share.Id, "docs", true, user.Id, readAllow, writeAllow);

        var acl = BuildAclService();
        Assert.True(await acl.HasAccessAsync(ctx, share.Id, "docs", true, FilePermission.CreateWriteData));

        // Revoke only the write grant.
        await AclRepo().DeleteAsync(writeAllow.Id);

        Assert.False(await acl.HasAccessAsync(ctx, share.Id, "docs", true, FilePermission.CreateWriteData));
        Assert.True(await acl.HasAccessAsync(ctx, share.Id, "docs", true, FilePermission.ListReadData)); // read survives
    }

    [Fact]
    public async Task InheritedAllow_OnParent_GrantsChild_AndRevokingParent_RemovesChildAccess()
    {
        var user = SeedUser("alice");
        var share = SeedShare("docs");
        var ctx = ContextFor(user);
        // Grant on the parent folder with full inheritance; the child inherits it.
        var parentAllow = new AccessEntry(user.Id, AclEntryType.Allow, FilePermission.ReadAll, AclInheritance.Everything);
        SeedFileWithAcl(share.Id, "docs", true, user.Id, parentAllow);
        SeedFileWithAcl(share.Id, "docs/report.txt", isDirectory: false, user.Id); // child, no own ACL

        var acl = BuildAclService();
        Assert.True(await acl.HasAccessAsync(ctx, share.Id, "docs/report.txt", false, FilePermission.ListReadData));

        // Revoke the inherited grant on the parent → child loses access too.
        await AclRepo().DeleteAsync(parentAllow.Id);

        Assert.False(await acl.HasAccessAsync(ctx, share.Id, "docs/report.txt", false, FilePermission.ListReadData));
    }

    // ══════════════════ Batch evaluation (HasAccessBatchAsync) ══════════════════

    private static IReadOnlyList<(string, bool)> Items(params string[] filePaths)
        => filePaths.Select(p => (p, false)).ToList();

    [Fact]
    public async Task Batch_ResolvesPerItem_InOneRoundTrip()
    {
        var user = SeedUser("alice");
        var share = SeedShare("docs");
        var ctx = ContextFor(user);
        // a.txt has its own grant; b.txt has none and inherits nothing.
        var aAllow = new AccessEntry(user.Id, AclEntryType.Allow, FilePermission.ReadAll, AclInheritance.Everything);
        SeedFileWithAcl(share.Id, "docs/a.txt", false, user.Id, aAllow);

        var acl = BuildAclService();
        var result = await acl.HasAccessBatchAsync(
            ctx, share.Id, Items("docs/a.txt", "docs/b.txt"), FilePermission.ListReadData);

        Assert.True(result[ShareRelativePath.Normalize("docs/a.txt")]);
        Assert.False(result[ShareRelativePath.Normalize("docs/b.txt")]);
    }

    [Fact]
    public async Task Batch_InheritsParentGrant_ThenRevokingParent_DeniesWholeBatch()
    {
        var user = SeedUser("alice");
        var share = SeedShare("docs");
        var ctx = ContextFor(user);
        // Grant on the parent folder with full inheritance — both children inherit it.
        var parentAllow = new AccessEntry(user.Id, AclEntryType.Allow, FilePermission.ReadAll, AclInheritance.Everything);
        SeedFileWithAcl(share.Id, "docs", true, user.Id, parentAllow);

        var acl = BuildAclService();
        var items = Items("docs/a.txt", "docs/b.txt");

        var granted = await acl.HasAccessBatchAsync(ctx, share.Id, items, FilePermission.ListReadData);
        Assert.All(granted.Values, Assert.True);

        // Revoke the parent grant → the entire batch loses access in the next evaluation.
        await AclRepo().DeleteAsync(parentAllow.Id);

        var revoked = await acl.HasAccessBatchAsync(ctx, share.Id, items, FilePermission.ListReadData);
        Assert.All(revoked.Values, Assert.False);
    }

    [Fact]
    public async Task Batch_DenyOnOneChild_OnlyThatItemIsBlocked()
    {
        var user = SeedUser("alice");
        var share = SeedShare("docs");
        var ctx = ContextFor(user);
        var parentAllow = new AccessEntry(user.Id, AclEntryType.Allow, FilePermission.ReadAll, AclInheritance.Everything);
        SeedFileWithAcl(share.Id, "docs", true, user.Id, parentAllow);
        // a.txt carries its own Deny, which must beat the inherited Allow — for a.txt only.
        var aDeny = new AccessEntry(user.Id, AclEntryType.Deny, FilePermission.ListReadData, AclInheritance.Everything);
        SeedFileWithAcl(share.Id, "docs/a.txt", false, user.Id, aDeny);

        var acl = BuildAclService();
        var result = await acl.HasAccessBatchAsync(
            ctx, share.Id, Items("docs/a.txt", "docs/b.txt"), FilePermission.ListReadData);

        Assert.False(result[ShareRelativePath.Normalize("docs/a.txt")]); // deny wins
        Assert.True(result[ShareRelativePath.Normalize("docs/b.txt")]);  // still inherits allow
    }

    [Fact]
    public async Task Batch_NullUser_DeniesEveryItem()
    {
        var share = SeedShare("docs");
        var acl = BuildAclService();

        var result = await acl.HasAccessBatchAsync(
            null!, share.Id, Items("docs/a.txt", "docs/b.txt"), FilePermission.ListReadData);

        Assert.All(result.Values, Assert.False);
    }

    [Fact]
    public async Task Batch_EmptyItemList_ReturnsEmpty()
    {
        var user = SeedUser("alice");
        var share = SeedShare("docs");
        var acl = BuildAclService();

        var result = await acl.HasAccessBatchAsync(
            ContextFor(user), share.Id, new List<(string, bool)>(), FilePermission.ListReadData);

        Assert.Empty(result);
    }

    // ══════════════════ Through the AclEditorViewModel ══════════════════

    private AclEditorViewModel BuildAclEditor(User actor, Guid shareId, out Mock<IManagementAuthService> mgmtAuth)
    {
        var ctx = ContextFor(actor);
        var auth = new Mock<IManagementAuthService>();
        auth.Setup(m => m.CanManageShareAsync(
                It.IsAny<UserContext>(), shareId, ManagementPermission.ManageShareAcls))
            .ReturnsAsync(true);
        mgmtAuth = auth;

        return new AclEditorViewModel(
            AclRepo(), FileMetadataRepo(), UserRepo(), GroupRepo(), RoleRepo(),
            auth.Object,
            UserContextFactoryFor((actor.Username, ctx)).Object,
            AuthStateFor(actor.Username),
            NullLogger<AclEditorViewModel>.Instance);
    }

    [Fact]
    public async Task AddThenDeleteEntry_ViaViewModel_FlipsRealAccess()
    {
        var admin = SeedUser("admin");
        var target = SeedUser("bob");
        var share = SeedShare("docs");
        var targetCtx = ContextFor(target);
        var acl = BuildAclService();

        var editor = BuildAclEditor(admin, share.Id, out _);
        await editor.LoadAsync("docs", share.Id, isDirectory: true);

        // Grant Bob read access through the editor.
        editor.NewPrincipalId = target.Id;
        editor.NewPermissions = FilePermission.ReadAll;
        Assert.True(await editor.AddEntryAsync());

        // The real service now sees the access.
        Assert.True(await acl.HasAccessAsync(targetCtx, share.Id, "docs", true, FilePermission.ListReadData));

        // Revoke it through the editor.
        var entryId = editor.Entries.Single().Id;
        Assert.True(await editor.DeleteEntryAsync(entryId));

        // Access is genuinely gone.
        Assert.False(await acl.HasAccessAsync(targetCtx, share.Id, "docs", true, FilePermission.ListReadData));
    }

    [Fact]
    public async Task UnauthorizedDelete_ViaViewModel_DoesNotRevokeAccess()
    {
        var admin = SeedUser("admin");
        var target = SeedUser("bob");
        var share = SeedShare("docs");
        var targetCtx = ContextFor(target);
        var acl = BuildAclService();

        var editor = BuildAclEditor(admin, share.Id, out var mgmtAuth);
        await editor.LoadAsync("docs", share.Id, true);
        editor.NewPrincipalId = target.Id;
        editor.NewPermissions = FilePermission.ReadAll;
        await editor.AddEntryAsync();
        var entryId = editor.Entries.Single().Id;

        // The actor loses ACL-management rights before attempting the revoke.
        mgmtAuth.Setup(m => m.CanManageShareAsync(
                It.IsAny<UserContext>(), share.Id, ManagementPermission.ManageShareAcls))
            .ReturnsAsync(false);

        var ok = await editor.DeleteEntryAsync(entryId);

        Assert.False(ok);
        // The grant must still be in force — an unauthorized revoke may not weaken access.
        Assert.True(await acl.HasAccessAsync(targetCtx, share.Id, "docs", true, FilePermission.ListReadData));
    }
}

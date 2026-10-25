using Kaimo_File_Server.Core.Domain.Identity;
using Kaimo_File_Server.Core.Helpers;
using Kaimo_File_Server.Core.Security;
using Moq;
using Xunit;

namespace Kaimo_File_Server.Tests;

/// <summary>
/// Joining a group confers every role the group holds. Adding members therefore needs the
/// same authority as assigning those roles directly; otherwise ManageGroupMembers would be a
/// back door around the no-privilege-elevation gate (including the Admins group mirror).
/// </summary>
public partial class RoleAssignmentServiceTests
{
    private Group SeedGroup(string name, Guid? id = null)
    {
        var group = new Group(id ?? Guid.NewGuid(), name);
        using var db = NewContext();
        db.Groups.Add(group);
        db.SaveChanges();
        return group;
    }

    private void SeedAssignment(Guid principalId, Role role, ScopeType scopeType, Guid scopeId)
    {
        using var db = NewContext();
        db.ScopedRoleAssignments.Add(new ScopedRoleAssignment(principalId, role.Id, scopeType, scopeId));
        db.SaveChanges();
    }

    private void DenyAssigning(ManagementPermission perms)
        => _mgmtAuth.Setup(m => m.CanAssignRoleAsync(It.IsAny<UserContext>(), perms,
                It.IsAny<ScopeType>(), It.IsAny<Guid>()))
            .ReturnsAsync(false);

    [Fact]
    public async Task CanAddGroupMembersAsync_GroupWithoutRoles_IsAllowed()
    {
        var actor = SeedUser("admin");
        var group = SeedGroup("plain");
        DenyAssigning(ManagementPermission.AccessHomeContents);

        Assert.True(await BuildSut().CanAddGroupMembersAsync(ContextFor(actor), group.Id));
    }

    [Fact]
    public async Task CanAddGroupMembersAsync_GroupRoleTheActorCannotAssign_IsDenied()
    {
        var actor = SeedUser("deptadmin");
        var group = SeedGroup("auditors");
        var dept = SeedDepartment("Sales");
        var role = SeedRole("HomeAuditor", ManagementPermission.AccessHomeContents);
        SeedAssignment(group.Id, role, ScopeType.Department, dept.Id);
        DenyAssigning(ManagementPermission.AccessHomeContents);

        Assert.False(await BuildSut().CanAddGroupMembersAsync(ContextFor(actor), group.Id));
    }

    [Fact]
    public async Task CanAddGroupMembersAsync_GroupRoleTheActorCanAssign_IsAllowed()
    {
        var actor = SeedUser("admin");
        var group = SeedGroup("helpdesk");
        var role = SeedRole("Helpdesk", ManagementPermission.ResetPasswords);
        SeedAssignment(group.Id, role, ScopeType.Global, Guid.Empty);
        DenyAssigning(ManagementPermission.AccessHomeContents);

        Assert.True(await BuildSut().CanAddGroupMembersAsync(ContextFor(actor), group.Id));
    }

    [Fact]
    public async Task CanAddGroupMembersAsync_AdminsGroup_NeedsTheRightToAssignTheAdminRole()
    {
        // The Admins group carries no assignment itself; joining it grants the admin role (mirror).
        var actor = SeedUser("groupadmin");
        SeedAdminsGroup();
        var adminRole = SeedRole("Administrator",
            ManagementPermission.FullAdmin | ManagementPermission.AccessHomeContents, WellKnownGUIDs.ROLE_ADMIN);
        DenyAssigning(adminRole.ManagementPermissions);

        Assert.False(await BuildSut().CanAddGroupMembersAsync(ContextFor(actor), WellKnownGUIDs.GROUP_ADMINS));
    }
}

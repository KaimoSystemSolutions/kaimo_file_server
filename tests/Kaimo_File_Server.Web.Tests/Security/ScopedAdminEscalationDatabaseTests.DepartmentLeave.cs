using Kaimo_File_Server.Core.Domain.Identity;
using Kaimo_File_Server.Core.Security.Management;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Kaimo_File_Server.Web.Tests.Security;

/// <summary>
/// Leaving a department drops the user's memberships in the department's groups and the roles
/// assigned to the user at that department. Both editors (department page and user form) warn
/// about this first and only write after the warning was confirmed.
/// </summary>
public partial class ScopedAdminEscalationDatabaseTests
{
    /// <summary>Bob is a member of <c>Own</c>, of a group of <c>Own</c>, holds a role at
    /// <c>Own</c> and another role at <c>Foreign</c>.</summary>
    private (User Bob, Group Team, Role OwnRole, Role ForeignRole) SeedMemberWithDepartmentTies()
    {
        var team = SeedGroup("Team", _own.Id);
        var bob = SeedUser("bob");
        AddToDepartment(_own, bob);
        SeedMembership(team, bob);
        var ownRole = SeedRole(ManagementPermission.ResetPasswords);
        var foreignRole = SeedRole(ManagementPermission.ViewDepartment);
        Assign(bob.Id, ownRole, ScopeType.Department, _own.Id);
        Assign(bob.Id, foreignRole, ScopeType.Department, _foreign.Id);
        return (bob, team, ownRole, foreignRole);
    }

    private async Task<bool> HasAssignmentAsync(User user, Role role, Guid scopeId)
    {
        await using var db = NewContext();
        return await db.ScopedRoleAssignments.AnyAsync(
            a => a.PrincipalId == user.Id && a.RoleId == role.Id && a.ScopeId == scopeId);
    }

    // ═══════════════════ Department page ═══════════════════

    [Fact]
    public async Task RemovingMemberWithDepartmentTies_WarnsFirstAndChangesNothing()
    {
        var (bob, team, ownRole, _) = SeedMemberWithDepartmentTies();

        var sut = await EditDepartmentAsync(_globalAdmin, _own);
        sut.EditMembers.Single(m => m.Item.Id == bob.Id).IsChecked = false;
        await sut.SaveAsync();

        Assert.Null(sut.ErrorMessage);
        var warning = Assert.Single(sut.PendingDepartmentLeaves);
        Assert.Equal(bob.Name, warning.Subject);
        Assert.Equal(["Team"], warning.GroupNames);
        Assert.Equal([ownRole.Name], warning.RoleNames);
        Assert.True(sut.IsEditing);
        Assert.True(await IsMemberAsync(_own, bob));
        Assert.True(await IsGroupMemberAsync(team, bob));
        Assert.True(await HasAssignmentAsync(bob, ownRole, _own.Id));
    }

    [Fact]
    public async Task ConfirmingDepartmentLeave_DropsGroupsAndDepartmentRolesOnly()
    {
        var (bob, team, ownRole, foreignRole) = SeedMemberWithDepartmentTies();

        var sut = await EditDepartmentAsync(_globalAdmin, _own);
        sut.EditMembers.Single(m => m.Item.Id == bob.Id).IsChecked = false;
        await sut.SaveAsync();
        await sut.ConfirmDepartmentLeaveAsync();

        Assert.Null(sut.ErrorMessage);
        Assert.Empty(sut.PendingDepartmentLeaves);
        Assert.False(await IsMemberAsync(_own, bob));
        Assert.False(await IsGroupMemberAsync(team, bob));
        Assert.False(await HasAssignmentAsync(bob, ownRole, _own.Id));
        Assert.True(await HasAssignmentAsync(bob, foreignRole, _foreign.Id));
    }

    [Fact]
    public async Task CancellingDepartmentLeave_KeepsEverythingAndStaysInEditMode()
    {
        var (bob, team, ownRole, _) = SeedMemberWithDepartmentTies();

        var sut = await EditDepartmentAsync(_globalAdmin, _own);
        sut.EditMembers.Single(m => m.Item.Id == bob.Id).IsChecked = false;
        await sut.SaveAsync();
        sut.CancelDepartmentLeave();

        Assert.Empty(sut.PendingDepartmentLeaves);
        Assert.True(sut.IsEditing);
        Assert.True(await IsMemberAsync(_own, bob));
        Assert.True(await IsGroupMemberAsync(team, bob));
        Assert.True(await HasAssignmentAsync(bob, ownRole, _own.Id));
    }

    [Fact]
    public async Task RemovingMemberWithoutDepartmentTies_SavesWithoutWarning()
    {
        var bob = SeedUser("bob");
        AddToDepartment(_own, bob);

        var sut = await EditDepartmentAsync(_globalAdmin, _own);
        sut.EditMembers.Single(m => m.Item.Id == bob.Id).IsChecked = false;
        await sut.SaveAsync();

        Assert.Null(sut.ErrorMessage);
        Assert.Empty(sut.PendingDepartmentLeaves);
        Assert.False(await IsMemberAsync(_own, bob));
    }

    [Fact]
    public async Task DeptAdmin_RemovingMemberWithDepartmentRoleBeyondOwnRights_IsRefused()
    {
        // Revoking the role needs AssignRoles, which the department admin's role lacks.
        var bob = SeedUser("bob");
        AddToDepartment(_own, bob);
        var role = SeedRole(ManagementPermission.ResetPasswords);
        Assign(bob.Id, role, ScopeType.Department, _own.Id);

        var sut = await EditDepartmentAsync(_deptAdmin, _own);
        sut.EditMembers.Single(m => m.Item.Id == bob.Id).IsChecked = false;
        await sut.SaveAsync();
        await sut.ConfirmDepartmentLeaveAsync();

        Assert.NotNull(sut.ErrorMessage);
        Assert.True(await IsMemberAsync(_own, bob));
        Assert.True(await HasAssignmentAsync(bob, role, _own.Id));
    }

    // ═══════════════════ User form ═══════════════════

    [Fact]
    public async Task UserForm_UncheckingDepartmentWithTies_WarnsFirstAndChangesNothing()
    {
        var (bob, team, ownRole, _) = SeedMemberWithDepartmentTies();

        var sut = await EditUserAsync(_globalAdmin, bob);
        sut.EditUserDepartments.Single(d => d.Item.Id == _own.Id).IsChecked = false;
        await sut.SaveUserAsync();

        Assert.Null(sut.ErrorMessage);
        var warning = Assert.Single(sut.PendingDepartmentLeaves);
        Assert.Equal(_own.Name, warning.Subject);
        Assert.Equal(["Team"], warning.GroupNames);
        Assert.Equal([ownRole.Name], warning.RoleNames);
        Assert.True(sut.IsEditing);
        Assert.True(await IsMemberAsync(_own, bob));
        Assert.True(await IsGroupMemberAsync(team, bob));
        Assert.True(await HasAssignmentAsync(bob, ownRole, _own.Id));
    }

    [Fact]
    public async Task UserForm_ConfirmingDepartmentLeave_DropsGroupsAndDepartmentRolesOnly()
    {
        var (bob, team, ownRole, foreignRole) = SeedMemberWithDepartmentTies();

        var sut = await EditUserAsync(_globalAdmin, bob);
        sut.EditUserDepartments.Single(d => d.Item.Id == _own.Id).IsChecked = false;
        await sut.SaveUserAsync();
        await sut.ConfirmDepartmentLeaveAsync();

        Assert.Null(sut.ErrorMessage);
        Assert.Empty(sut.PendingDepartmentLeaves);
        Assert.False(await IsMemberAsync(_own, bob));
        Assert.False(await IsGroupMemberAsync(team, bob));
        Assert.False(await HasAssignmentAsync(bob, ownRole, _own.Id));
        Assert.True(await HasAssignmentAsync(bob, foreignRole, _foreign.Id));
    }

    [Fact]
    public async Task UserForm_CancellingDepartmentLeave_KeepsEverything()
    {
        var (bob, team, ownRole, _) = SeedMemberWithDepartmentTies();

        var sut = await EditUserAsync(_globalAdmin, bob);
        sut.EditUserDepartments.Single(d => d.Item.Id == _own.Id).IsChecked = false;
        await sut.SaveUserAsync();
        sut.CancelDepartmentLeave();

        Assert.Empty(sut.PendingDepartmentLeaves);
        Assert.True(sut.IsEditing);
        Assert.True(await IsMemberAsync(_own, bob));
        Assert.True(await IsGroupMemberAsync(team, bob));
        Assert.True(await HasAssignmentAsync(bob, ownRole, _own.Id));
    }
}

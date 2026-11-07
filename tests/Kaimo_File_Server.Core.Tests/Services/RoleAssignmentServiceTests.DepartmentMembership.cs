using Kaimo_File_Server.Core.Domain.Identity;
using Kaimo_File_Server.Core.Language;
using Kaimo_File_Server.Core.Security;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Kaimo_File_Server.Core.Tests.Services;

/// <summary>
/// A role assigned at a department only goes to members: a user must belong to exactly that
/// department, a group must be one of its groups. Leaving the department revokes such roles, so
/// an assignment to a non-member could never be cleaned up. System-wide and share scopes are
/// not affected.
/// </summary>
public partial class RoleAssignmentServiceTests
{
    private Group SeedDepartmentGroup(string name, Guid departmentId)
    {
        var group = new Group(Guid.NewGuid(), name, departmentId);
        using var db = NewContext();
        db.Groups.Add(group);
        db.SaveChanges();
        return group;
    }

    private async Task<bool> AnyAssignmentForAsync(Guid principalId)
    {
        await using var db = NewContext();
        return await db.ScopedRoleAssignments.AnyAsync(a => a.PrincipalId == principalId);
    }

    [Fact]
    public async Task CreateAsync_DepartmentScope_UserNotMember_IsRefused()
    {
        var actor = SeedUser("admin");
        var bob = SeedUser("bob");
        var sales = SeedDepartment("Sales");

        var error = await BuildSut().CreateAsync(
            ContextFor(actor), bob.Id, SeedRole("Reviewer"), ScopeType.Department, sales.Id);

        Assert.Equal(Resources.Web_Error_AssignmentRequiresMembership, error);
        Assert.False(await AnyAssignmentForAsync(bob.Id));
    }

    [Fact]
    public async Task CreateAsync_DepartmentScope_UserMember_IsStored()
    {
        var actor = SeedUser("admin");
        var bob = SeedUser("bob");
        var sales = SeedDepartment("Sales");
        await DepartmentRepo().AddUserAsync(sales.Id, bob.Id);

        var error = await BuildSut().CreateAsync(
            ContextFor(actor), bob.Id, SeedRole("Reviewer"), ScopeType.Department, sales.Id);

        Assert.Null(error);
        Assert.True(await AnyAssignmentForAsync(bob.Id));
    }

    [Fact]
    public async Task CreateAsync_DepartmentScope_MemberOfSubdepartmentOnly_IsRefused()
    {
        var actor = SeedUser("admin");
        var bob = SeedUser("bob");
        var sales = SeedDepartment("Sales");
        var north = SeedDepartment("North", parentId: sales.Id);
        await DepartmentRepo().AddUserAsync(north.Id, bob.Id);

        var error = await BuildSut().CreateAsync(
            ContextFor(actor), bob.Id, SeedRole("Reviewer"), ScopeType.Department, sales.Id);

        Assert.Equal(Resources.Web_Error_AssignmentRequiresMembership, error);
        Assert.False(await AnyAssignmentForAsync(bob.Id));
    }

    [Fact]
    public async Task CreateAsync_DepartmentScope_GroupOfThatDepartment_IsStored()
    {
        var actor = SeedUser("admin");
        var sales = SeedDepartment("Sales");
        var team = SeedDepartmentGroup("SalesTeam", sales.Id);

        var error = await BuildSut().CreateAsync(
            ContextFor(actor), team.Id, SeedRole("Reviewer"), ScopeType.Department, sales.Id);

        Assert.Null(error);
        Assert.True(await AnyAssignmentForAsync(team.Id));
    }

    [Fact]
    public async Task CreateAsync_DepartmentScope_GroupOfOtherDepartment_IsRefused()
    {
        var actor = SeedUser("admin");
        var sales = SeedDepartment("Sales");
        var hr = SeedDepartment("HR");
        var team = SeedDepartmentGroup("HrTeam", hr.Id);

        var error = await BuildSut().CreateAsync(
            ContextFor(actor), team.Id, SeedRole("Reviewer"), ScopeType.Department, sales.Id);

        Assert.Equal(Resources.Web_Error_AssignmentRequiresMembership, error);
        Assert.False(await AnyAssignmentForAsync(team.Id));
    }

    [Fact]
    public async Task CreateAsync_ShareScope_NonMember_IsStored()
    {
        var actor = SeedUser("admin");
        var bob = SeedUser("bob");
        var share = SeedShare("docs");

        var error = await BuildSut().CreateAsync(
            ContextFor(actor), bob.Id, SeedRole("Reviewer"), ScopeType.Share, share.Id);

        Assert.Null(error);
        Assert.True(await AnyAssignmentForAsync(bob.Id));
    }
}

using Kaimo_File_Server.Core.Security.Management;
using Xunit;

namespace Kaimo_File_Server.Web.Tests.Security;

/// <summary>
/// The scoped role assignment forms only offer department scopes the principal belongs to:
/// the department page lists its members and groups, the user page the user's departments, and
/// the role page the departments of the chosen user or group.
/// </summary>
public partial class ScopedAdminEscalationDatabaseTests
{
    [Fact]
    public async Task DepartmentPage_AddAssignment_OffersOnlyMembersAndOwnGroups()
    {
        var bob = SeedUser("bob");
        var carol = SeedUser("carol");
        AddToDepartment(_own, bob);
        AddToDepartment(_foreign, carol);
        var ownTeam = SeedGroup("OwnTeam", _own.Id);
        var foreignTeam = SeedGroup("ForeignTeam", _foreign.Id);

        var sut = await SelectDepartmentAsync(_globalAdmin, _own);
        await sut.StartAddAssignmentAsync();

        Assert.Contains(sut.AllUsers, u => u.Id == bob.Id);
        Assert.DoesNotContain(sut.AllUsers, u => u.Id == carol.Id);
        Assert.Contains(sut.AllGroups, g => g.Id == ownTeam.Id);
        Assert.DoesNotContain(sut.AllGroups, g => g.Id == foreignTeam.Id);
    }

    [Fact]
    public async Task UserPage_AddAssignment_OffersOnlyTheUsersDepartments()
    {
        var bob = SeedUser("bob");
        AddToDepartment(_own, bob);

        var sut = BuildUserList(_globalAdmin);
        await sut.LoadAsync();
        await sut.SelectUserAsync(sut.Users.Single(u => u.Id == bob.Id));
        sut.StartAddAssignment();
        sut.NewAssignmentPrincipalId = bob.Id;
        await sut.LoadAssignmentFormDataAsync();

        Assert.Contains(sut.AssignmentDepartments, d => d.Id == _own.Id);
        Assert.DoesNotContain(sut.AssignmentDepartments, d => d.Id == _foreign.Id);
        Assert.DoesNotContain(sut.AssignmentDepartments, d => d.Id == _ownChild.Id);
    }

    [Fact]
    public async Task RolePage_AddAssignment_OffersTheChosenGroupsDepartment()
    {
        var team = SeedGroup("ForeignTeam", _foreign.Id);

        var sut = await SelectRoleAsync(_globalAdmin, SeedRole(ManagementPermission.ResetPasswords));
        sut.StartAddAssignment();
        await sut.LoadAssignmentFormDataAsync();
        sut.NewAssignmentPrincipalId = team.Id;

        Assert.Contains(sut.AssignmentDepartments, d => d.Id == _foreign.Id);
        Assert.DoesNotContain(sut.AssignmentDepartments, d => d.Id == _own.Id);
    }

    [Fact]
    public async Task RolePage_ChangingPrincipal_ClearsDepartmentItDoesNotBelongTo()
    {
        var bob = SeedUser("bob");
        var carol = SeedUser("carol");
        AddToDepartment(_own, bob);
        AddToDepartment(_foreign, carol);

        var sut = await SelectRoleAsync(_globalAdmin, SeedRole(ManagementPermission.ResetPasswords));
        sut.StartAddAssignment();
        await sut.LoadAssignmentFormDataAsync();
        sut.NewAssignmentPrincipalId = bob.Id;
        sut.NewAssignmentScopeId = _own.Id;
        sut.NewAssignmentPrincipalId = carol.Id;

        Assert.Null(sut.NewAssignmentScopeId);
    }
}

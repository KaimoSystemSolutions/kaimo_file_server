using Kaimo_File_Server.Core.Security;
using Xunit;

namespace Kaimo_File_Server.Tests;

/// <summary>
/// Phase 4 (F15): view models keep the actor context of their page load, but authority that
/// came through a group ends as soon as the membership is removed — not only after a reload.
/// </summary>
public partial class ScopedAdminEscalationDatabaseTests
{
    [Fact]
    public async Task AuthorityViaGroup_EndsWithTheMembership_WithoutPageReload()
    {
        var admins = SeedGroup("Own admins", _own.Id);
        Assign(admins.Id, SeedNamedRole("Own admin role", ManagementPermission.DepartmentAdmin),
            ScopeType.Department, _own.Id);
        var helper = SeedUser("helper");
        AddToDepartment(_own, helper);
        SeedMembership(admins, helper);
        var bob = SeedUser("bob");
        AddToDepartment(_own, bob);

        var sut = await EditUserAsync(helper, bob);
        await GroupRepo().RemoveMemberAsync(admins.Id, helper.Id);

        sut.EditUserDescription = "after removal";
        await sut.SaveUserAsync();

        Assert.NotNull(sut.ErrorMessage);
        Assert.NotEqual("after removal", (await StoredUserAsync(bob)).Description);
    }
}

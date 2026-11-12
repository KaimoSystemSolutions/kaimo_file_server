using Kaimo_File_Server.Core.Domain.Department;
using Kaimo_File_Server.Core.Domain.Identity;
using Kaimo_File_Server.Core.Security.Authentication;
using Kaimo_File_Server.Core.Security.Management;
using Kaimo_File_Server.Infrastructure.Configuration;
using Kaimo_File_Server.Infrastructure.Repositories;
using Kaimo_File_Server.Infrastructure.Services;
using Microsoft.Extensions.Caching.Memory;
using Xunit;

namespace Kaimo_File_Server.Infrastructure.Tests.Services;

/// <summary>
/// <see cref="SecondFactorPolicyService"/> over the real schema: which users the policy targets
/// (all, user, group, role in any scope, department with sub-departments, management roles),
/// the login step that follows from it, and the mode / break-glass switches.
/// </summary>
public class SecondFactorPolicyServiceTests : DatabaseTestBase
{
    private static readonly DateTime Now = new(2026, 11, 1, 12, 0, 0, DateTimeKind.Utc);

    private SecondFactorPolicyService Service() => new(
        new ConfigRepository(DbFactory, new MemoryCache(new MemoryCacheOptions())),
        new UserContextFactory(DbFactory), DepartmentRepo(), ScopedRoleRepo(), RoleRepo(),
        new SecondFactorRepository(DbFactory), new MemoryCache(new MemoryCacheOptions()));

    private Role SeedRole(string name, ManagementPermission permissions = ManagementPermission.None)
    {
        var role = new Role(Guid.NewGuid(), name, permissions);
        using var db = NewContext();
        db.Roles.Add(role);
        db.SaveChanges();
        return role;
    }

    private static SecondFactorPolicy Required(Action<SecondFactorPolicy>? configure = null)
    {
        var policy = new SecondFactorPolicy { Mode = SecondFactorMode.Required, RequireForManagementRoles = false };
        configure?.Invoke(policy);
        return policy;
    }

    [Fact]
    public async Task Default_IsOptional_AndRequiresNobody()
    {
        var service = Service();
        var user = SeedUser("erika");

        Assert.Equal(SecondFactorMode.Optional, (await service.GetPolicyAsync()).Mode);
        Assert.False(await service.IsRequiredAsync(ContextFor(user)));
        Assert.Equal(SecondFactorLoginStep.None, await service.GetLoginStepAsync(ContextFor(user)));
    }

    [Fact]
    public async Task AppliesToAll_RequiresEveryUser()
    {
        var user = SeedUser("erika");
        Assert.True(await Service().IsRequiredAsync(ContextFor(user), Required(p => p.AppliesToAll = true)));
    }

    [Fact]
    public async Task UserList_RequiresOnlyListedUsers()
    {
        var listed = SeedUser("erika");
        var other = SeedUser("max");
        var policy = Required(p => p.UserIds.Add(listed.Id));

        Assert.True(await Service().IsRequiredAsync(ContextFor(listed), policy));
        Assert.False(await Service().IsRequiredAsync(ContextFor(other), policy));
    }

    [Fact]
    public async Task Group_RequiresItsMembers()
    {
        var user = SeedUser("erika");
        var group = new Group(Guid.NewGuid(), "Finance");
        var member = new UserContext(user, [group], [], []);
        var outsider = new UserContext(SeedUser("max"), [], [], []);
        var policy = Required(p => p.GroupIds.Add(group.Id));

        Assert.True(await Service().IsRequiredAsync(member, policy));
        Assert.False(await Service().IsRequiredAsync(outsider, policy));
    }

    [Fact]
    public async Task Role_CountsInAnyScope_DirectAndThroughGroups()
    {
        var role = SeedRole("Auditor");
        var dept = SeedDepartment("Sales");
        var direct = SeedUser("erika");
        await ScopedRoleRepo().CreateAsync(new ScopedRoleAssignment(direct.Id, role.Id, ScopeType.Department, dept.Id));

        var groupUser = SeedUser("max");
        var group = new Group(Guid.NewGuid(), "Auditors");
        await ScopedRoleRepo().CreateAsync(ScopedRoleAssignment.Global(group.Id, role.Id));

        var policy = Required(p => p.RoleIds.Add(role.Id));

        Assert.True(await Service().IsRequiredAsync(ContextFor(direct), policy));
        Assert.True(await Service().IsRequiredAsync(new UserContext(groupUser, [group], [], []), policy));
        Assert.False(await Service().IsRequiredAsync(ContextFor(SeedUser("nobody")), policy));
    }

    [Fact]
    public async Task Department_IncludesSubDepartments()
    {
        var parent = SeedDepartment("Company");
        var child = SeedDepartment("Engineering", parentId: parent.Id);
        var user = SeedUser("erika");
        var policy = Required(p => p.DepartmentIds.Add(parent.Id));

        Assert.True(await Service().IsRequiredAsync(ContextFor(user, child), policy));
        Assert.False(await Service().IsRequiredAsync(ContextFor(SeedUser("max"), SeedDepartment("Other")), policy));
    }

    [Fact]
    public async Task ManagementRoles_AreRequiredWhenEnabled()
    {
        var admin = SeedUser("admin");
        var role = SeedRole("Helpdesk", ManagementPermission.ResetPasswords);
        await ScopedRoleRepo().CreateAsync(ScopedRoleAssignment.Global(admin.Id, role.Id));
        var plainRoleUser = SeedUser("reader");
        await ScopedRoleRepo().CreateAsync(ScopedRoleAssignment.Global(plainRoleUser.Id, SeedRole("Reader").Id));

        Assert.True(await Service().IsRequiredAsync(ContextFor(admin), Required(p => p.RequireForManagementRoles = true)));
        Assert.False(await Service().IsRequiredAsync(ContextFor(plainRoleUser), Required(p => p.RequireForManagementRoles = true)));
        Assert.False(await Service().IsRequiredAsync(ContextFor(admin), Required()));
    }

    [Fact]
    public async Task OptionalMode_NeverRequires()
    {
        var user = SeedUser("erika");
        var policy = new SecondFactorPolicy { Mode = SecondFactorMode.Optional, AppliesToAll = true };
        Assert.False(await Service().IsRequiredAsync(ContextFor(user), policy));
    }

    [Fact]
    public async Task LoginStep_FollowsEnrollmentAndPolicy()
    {
        var service = Service();
        var enrolled = SeedUser("erika");
        var notEnrolled = SeedUser("max");
        await new SecondFactorRepository(DbFactory).ConfirmAsync(enrolled.Id, "secret", 1, [], Now);
        await service.SavePolicyAsync(Required(p => p.AppliesToAll = true));

        Assert.Equal(SecondFactorLoginStep.Verify, await service.GetLoginStepAsync(ContextFor(enrolled)));
        Assert.Equal(SecondFactorLoginStep.Enroll, await service.GetLoginStepAsync(ContextFor(notEnrolled)));
        Assert.True(await service.SessionRequiresSecondFactorAsync(enrolled.Id));
        Assert.True(await service.SessionRequiresSecondFactorAsync(notEnrolled.Id));
    }

    [Fact]
    public async Task OptionalMode_AsksEnrolledUsersOnly()
    {
        var service = Service();
        var enrolled = SeedUser("erika");
        await new SecondFactorRepository(DbFactory).ConfirmAsync(enrolled.Id, "secret", 1, [], Now);

        Assert.Equal(SecondFactorLoginStep.Verify, await service.GetLoginStepAsync(ContextFor(enrolled)));
        Assert.Equal(SecondFactorLoginStep.None, await service.GetLoginStepAsync(ContextFor(SeedUser("max"))));
    }

    [Fact]
    public async Task OffMode_AsksNobody_EvenEnrolledUsers()
    {
        var service = Service();
        var enrolled = SeedUser("erika");
        await new SecondFactorRepository(DbFactory).ConfirmAsync(enrolled.Id, "secret", 1, [], Now);
        await service.SavePolicyAsync(new SecondFactorPolicy { Mode = SecondFactorMode.Off });

        Assert.Equal(SecondFactorLoginStep.None, await service.GetLoginStepAsync(ContextFor(enrolled)));
        Assert.False(await service.SessionRequiresSecondFactorAsync(enrolled.Id));
        Assert.False(await service.IsAvailableAsync());
    }

    [Fact]
    public async Task BreakGlassEnvironmentVariable_AsksNobody()
    {
        var service = Service();
        var enrolled = SeedUser("erika");
        await new SecondFactorRepository(DbFactory).ConfirmAsync(enrolled.Id, "secret", 1, [], Now);

        Environment.SetEnvironmentVariable(SecondFactorPolicy.DisableEnvironmentVariable, "true");
        try
        {
            Assert.Equal(SecondFactorLoginStep.None, await service.GetLoginStepAsync(ContextFor(enrolled)));
        }
        finally
        {
            Environment.SetEnvironmentVariable(SecondFactorPolicy.DisableEnvironmentVariable, null);
        }
    }

    [Fact]
    public async Task SavingThePolicy_DropsCachedSessionResults()
    {
        // Required → Optional must not keep refusing the password-only session of a user
        // without a factor until the cached "required" result expires.
        var service = Service();
        var user = SeedUser("erika");
        await service.SavePolicyAsync(Required(p => p.AppliesToAll = true));
        Assert.True(await service.SessionRequiresSecondFactorAsync(user.Id));

        await service.SavePolicyAsync(new SecondFactorPolicy { Mode = SecondFactorMode.Optional });

        Assert.False(await service.SessionRequiresSecondFactorAsync(user.Id));
    }

    [Fact]
    public async Task SavedPolicy_RoundTripsThroughTheConfigStore()
    {
        var group = Guid.NewGuid();
        await Service().SavePolicyAsync(Required(p => { p.GroupIds.Add(group); p.Issuer = "Acme Files"; }));

        var loaded = await Service().GetPolicyAsync();
        Assert.Equal(SecondFactorMode.Required, loaded.Mode);
        Assert.Contains(group, loaded.GroupIds);
        Assert.Equal("Acme Files", loaded.EffectiveIssuer);
    }

    [Theory]
    [InlineData("", SecondFactorPolicy.DefaultIssuer)]
    [InlineData("   ", SecondFactorPolicy.DefaultIssuer)]
    [InlineData("  Acme  ", "Acme")]
    public void EffectiveIssuer_IsTrimmedAndNeverEmpty(string issuer, string expected)
    {
        Assert.Equal(expected, new SecondFactorPolicy { Issuer = issuer }.EffectiveIssuer);
    }
}

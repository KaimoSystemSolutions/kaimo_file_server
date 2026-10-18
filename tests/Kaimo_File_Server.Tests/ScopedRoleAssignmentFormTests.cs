using Bunit;
using Kaimo_File_Server.Core.Helpers;
using Kaimo_File_Server.Core.Security;
using Kaimo_File_Server.Web.Components.Shared;
using Kaimo_File_Server.Web.Services;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using Xunit;

namespace Kaimo_File_Server.Tests;

/// <summary>
/// Roles with system-level permissions only take full effect system-wide, so the assignment
/// form offers them only when "System wide" is chosen.
/// </summary>
public class ScopedRoleAssignmentFormTests
{
    private static readonly PrincipalPickerItem SystemRole = new(Guid.NewGuid(), "Administrator", PrincipalKind.Role);
    private static readonly PrincipalPickerItem DepartmentRole = new(Guid.NewGuid(), "DepartmentAdmin", PrincipalKind.Role);
    private static readonly PrincipalPickerItem[] Roles = [SystemRole, DepartmentRole];

    private static readonly Dictionary<Guid, ManagementPermission> Permissions = new()
    {
        [SystemRole.Id] = ManagementPermission.FullAdmin,
        [DepartmentRole.Id] = ManagementPermission.DepartmentAdmin,
    };

    [Fact]
    public void IsSystemOnly_FlagsRolesWithSystemLevelPermissions()
    {
        Assert.True(ScopedRoleAssignmentForm.IsSystemOnly(ManagementPermission.FullAdmin));
        Assert.True(ScopedRoleAssignmentForm.IsSystemOnly(ManagementPermission.ManageCertificates));
        Assert.False(ScopedRoleAssignmentForm.IsSystemOnly(ManagementPermission.DepartmentAdmin));
        Assert.False(ScopedRoleAssignmentForm.IsSystemOnly(ManagementPermission.None));
    }

    [Fact]
    public void OfferedRoles_SystemWide_KeepsEveryRole()
    {
        var offered = ScopedRoleAssignmentForm.OfferedRoles(Roles, Permissions, systemWide: true);

        Assert.Equal([SystemRole, DepartmentRole], offered);
    }

    [Fact]
    public void OfferedRoles_DepartmentOrShare_HidesSystemOnlyRoles()
    {
        var offered = ScopedRoleAssignmentForm.OfferedRoles(Roles, Permissions, systemWide: false);

        Assert.Equal([DepartmentRole], offered);
    }

    [Fact]
    public void OfferedRoles_WithoutPermissionMap_KeepsEveryRole()
    {
        var offered = ScopedRoleAssignmentForm.OfferedRoles(Roles, null, systemWide: false);

        Assert.Equal([SystemRole, DepartmentRole], offered);
    }

    [Fact]
    public void SwitchingToDepartment_ClearsASelectedSystemOnlyRole()
    {
        using var ctx = NewContext();
        Guid? roleId = SystemRole.Id;
        var cut = ctx.Render<ScopedRoleAssignmentForm>(p => p
            .Add(f => f.Roles, Roles)
            .Add(f => f.RolePermissionsById, Permissions)
            .Add(f => f.RoleId, roleId)
            .Add(f => f.RoleIdChanged, id => roleId = id)
            .Add(f => f.Departments, [])
            .Add(f => f.ScopeType, ScopeType.Department)
            .Add(f => f.ScopeId, WellKnownGUIDs.DEPARTMENT_GLOBAL));

        cut.FindAll("button[role=radio]")[1].Click();

        Assert.Null(roleId);
    }

    [Fact]
    public void FixedSystemOnlyRole_SelectsSystemWideAndDisablesTheOtherScopes()
    {
        using var ctx = NewContext();
        Guid? scopeId = null;
        var cut = ctx.Render<ScopedRoleAssignmentForm>(p => p
            .Add(f => f.RolePermissions, ManagementPermission.FullAdmin)
            .Add(f => f.Departments, [])
            .Add(f => f.Shares, [])
            .Add(f => f.ScopeType, ScopeType.Department)
            .Add(f => f.ScopeIdChanged, id => scopeId = id));

        Assert.Equal(WellKnownGUIDs.DEPARTMENT_GLOBAL, scopeId);
        var choices = cut.FindAll("button[role=radio]");
        Assert.False(choices[0].HasAttribute("disabled"));
        Assert.True(choices[1].HasAttribute("disabled"));
        Assert.True(choices[2].HasAttribute("disabled"));
    }

    private static BunitContext NewContext()
    {
        var ctx = new BunitContext();
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        var env = new Mock<Microsoft.AspNetCore.Hosting.IWebHostEnvironment>();
        env.SetupGet(e => e.WebRootPath).Returns(MailNotificationTests.FindWebRoot());
        ctx.Services.AddSingleton(new AssetProvider(env.Object));
        return ctx;
    }
}

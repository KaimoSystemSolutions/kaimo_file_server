using Kaimo_File_Server.Core.Security;
using Kaimo_File_Server.Web.Components.ViewModels;
using Xunit;

namespace Kaimo_File_Server.Tests;

/// <summary>
/// The grouped role permission editor: the "Global only" marker and the per-area
/// select-all checkbox with its granted counter.
/// </summary>
public class RolePermissionEditorTests
{
    private static UserListViewModel NewVm(ManagementPermission permissions)
    {
        var vm = (UserListViewModel)System.Runtime.CompilerServices.RuntimeHelpers
            .GetUninitializedObject(typeof(UserListViewModel));
        vm.EditRolePermissions = permissions;
        return vm;
    }

    private static PermissionGroup UserGroup
        => UserListViewModel.PermissionGroups.First(g => g.Flags.Any(f => f.Flag == ManagementPermission.CreateUsers));

    [Fact]
    public void GlobalOnly_MarksExactlyThePermissionsEvaluatedSystemWide()
    {
        const ManagementPermission expected =
            ManagementPermission.ManageSystemSettings | ManagementPermission.ManageDataServices
            | ManagementPermission.ManageCertificates | ManagementPermission.ViewSystemLogs
            | ManagementPermission.ManageBackups | ManagementPermission.ViewSecurityMonitor
            | ManagementPermission.ManageMailServer | ManagementPermission.ManageNotifications
            | ManagementPermission.ManageClientDevices | ManagementPermission.ManageHomes
            | ManagementPermission.ManageConnections | ManagementPermission.UseConnections
            | ManagementPermission.ManageBackupRepositories;

        Assert.Equal(expected, UserListViewModel.GlobalOnlyPermissions);
    }

    [Theory]
    [InlineData(ManagementPermission.ManageHomes, true)]
    [InlineData(ManagementPermission.ViewSystemLogs, true)]
    [InlineData(ManagementPermission.CreateUsers, false)]
    [InlineData(ManagementPermission.AccessHomeContents, false)]
    [InlineData(ManagementPermission.ManageBackupJobs, false)]
    public void IsGlobalOnly_PerFlag(ManagementPermission flag, bool expected)
        => Assert.Equal(expected, UserListViewModel.IsGlobalOnly(flag));

    [Fact]
    public void EveryGlobalOnlyBit_IsShownInTheEditor()
    {
        var shown = UserListViewModel.PermissionGroups.SelectMany(g => g.Flags)
            .Aggregate(ManagementPermission.None, (all, f) => all | f.Flag);
        Assert.Equal(ManagementPermission.None, UserListViewModel.GlobalOnlyPermissions & ~shown);
    }

    [Fact]
    public void EveryGroup_HasAnIcon()
        => Assert.All(UserListViewModel.PermissionGroups, g => Assert.EndsWith(".svg", g.Icon));

    [Fact]
    public void GrantedCount_CountsOnlyTheGroupsFlags()
    {
        var vm = NewVm(ManagementPermission.CreateUsers | ManagementPermission.ResetPasswords
                       | ManagementPermission.ManageCertificates);
        Assert.Equal(2, vm.GrantedCount(UserGroup));
    }

    [Fact]
    public void ToggleGroupOn_GrantsAllFlagsOfTheGroup_AndKeepsOthers()
    {
        var vm = NewVm(ManagementPermission.ManageCertificates | ManagementPermission.CreateUsers);
        vm.ToggleEditGroup(UserGroup, true);

        Assert.Equal(UserGroup.Flags.Count, vm.GrantedCount(UserGroup));
        Assert.True(vm.HasEditPermission(ManagementPermission.ManageCertificates));
    }

    [Fact]
    public void ToggleGroupOff_RevokesOnlyTheGroupsFlags()
    {
        var vm = NewVm(ManagementPermission.UserAdmin | ManagementPermission.ManageHomes
                       | ManagementPermission.ManageCertificates);
        vm.ToggleEditGroup(UserGroup, false);

        Assert.Equal(0, vm.GrantedCount(UserGroup));
        Assert.Equal(ManagementPermission.ManageCertificates, vm.EditRolePermissions);
    }
}

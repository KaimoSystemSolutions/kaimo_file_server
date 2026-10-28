using Kaimo_File_Server.Core.Security;
using Kaimo_File_Server.Web.Components.ViewModels;
using Xunit;

namespace Kaimo_File_Server.Tests;

/// <summary>
/// The role editor's presets and checkboxes: a preset must only set bits the editor shows
/// (otherwise they are granted invisibly and can never be unchecked), and "FullAdmin" must
/// grant everything the built-in Administrator role holds.
/// </summary>
public class RoleEditorPresetTests
{
    private static ManagementPermission ShownBits => UserListViewModel.PermissionGroups
        .SelectMany(g => g.Flags)
        .Aggregate(ManagementPermission.None, (all, f) => all | f.Flag);

    [Fact]
    public void FullAdminPreset_GrantsEverythingOfTheAdministratorRole()
    {
        var preset = UserListViewModel.PermissionPresets.Single(p => p.Label == "FullAdmin");
        Assert.Equal(ManagementPermission.Administrator, preset.Permissions);
    }

    [Fact]
    public void AdministratorRole_KeepsTheFullAdminMask()
        => Assert.Equal(ManagementPermission.FullAdmin,
            ManagementPermission.Administrator & ManagementPermission.FullAdmin);

    [Theory]
    [InlineData(ManagementPermission.ManageHomes)]
    [InlineData(ManagementPermission.AccessHomeContents)]
    [InlineData(ManagementPermission.ManageMailServer)]
    [InlineData(ManagementPermission.ManageNotifications)]
    [InlineData(ManagementPermission.FileBackupAdmin)]
    public void AdministratorRole_IncludesBitsOutsideFullAdmin(ManagementPermission bit)
        => Assert.Equal(bit, ManagementPermission.Administrator & bit);

    [Fact]
    public void EveryPresetBit_IsShownAsCheckbox()
    {
        foreach (var preset in UserListViewModel.PermissionPresets)
            Assert.True((preset.Permissions & ~ShownBits) == ManagementPermission.None,
                $"{preset.Label} sets hidden bits: {preset.Permissions & ~ShownBits}");
    }

    [Fact]
    public void ApplyingFullAdmin_ChecksEveryCheckbox()
    {
        var vm = (UserListViewModel)System.Runtime.CompilerServices.RuntimeHelpers
            .GetUninitializedObject(typeof(UserListViewModel));
        vm.ApplyPermissionPreset(ManagementPermission.Administrator);

        Assert.All(UserListViewModel.PermissionGroups.SelectMany(g => g.Flags),
            f => Assert.True(vm.HasEditPermission(f.Flag), f.Label));
    }
}

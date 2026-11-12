using Kaimo_File_Server.Core.Security.Management;
using Kaimo_File_Server.Web.Components.ViewModels;
using Xunit;

namespace Kaimo_File_Server.Web.Tests.Layout;

/// <summary>
/// The sidebar Settings link is shown only when the global permission mask unlocks at least
/// one Settings tab — the same rule the Settings page uses for <c>CanAccessPage</c>.
/// </summary>
public class SettingsNavGateTests
{
    public static TheoryData<ManagementPermission> TabPermissions =>
    [
        ManagementPermission.ManageSystemSettings,
        ManagementPermission.ManageDataServices,
        ManagementPermission.ManageCertificates,
        ManagementPermission.ViewSystemLogs,
        ManagementPermission.ManageBackups,
        ManagementPermission.ManageClientDevices,
        ManagementPermission.ManageMailServer,
    ];

    [Theory]
    [MemberData(nameof(TabPermissions))]
    public void AnySingleTabPermission_GrantsAccess(ManagementPermission permission)
        => Assert.True(SettingsViewModel.CanAccessWith(permission));

    [Fact]
    public void NoPermission_DeniesAccess()
        => Assert.False(SettingsViewModel.CanAccessWith(ManagementPermission.None));

    [Fact]
    public void UnrelatedPermissions_DenyAccess()
        => Assert.False(SettingsViewModel.CanAccessWith(
            ManagementPermission.SyncAdmin | ManagementPermission.CreateUsers | ManagementPermission.ManageHomes));
}

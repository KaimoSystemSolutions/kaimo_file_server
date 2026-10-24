using Kaimo_File_Server.Core.Services;
using Kaimo_File_Server.Core.Storage;
using Kaimo_File_Server.Web.Components.ViewModels;
using Moq;
using Xunit;

namespace Kaimo_File_Server.Tests;

/// <summary>
/// <see cref="SettingsViewModel.LoadHomeFolderSettingsAsync"/>: the /home-folders page loads only
/// what its settings tab needs (permissions, pools, home share, recycle default), not the
/// whole settings page.
/// </summary>
public partial class SettingsViewModelSmbTests
{
    [Fact]
    public async Task LoadHomeFolderSettingsAsync_WithSettingsPermission_LoadsPoolsOnly()
    {
        var pools = new List<StorageUsageInfo> { new("/data/storage/pool01", "/dev/sda", 1_000, 400, 600, true) };
        var systemInfo = new Mock<ISystemInfoService>();
        systemInfo.Setup(s => s.GetStorageUsage()).Returns(pools);
        var h = new Harness(canManageDataServices: false, canManageSettings: true, systemInfo: systemInfo.Object);
        h.Config.Setup(c => c.GetAsync(StoragePoolNaming.ConfigKey, It.IsAny<Dictionary<string, string>>()))
            .ReturnsAsync(new Dictionary<string, string>());

        await h.Vm.LoadHomeFolderSettingsAsync();

        Assert.True(h.Vm.CanManageSettings);
        Assert.Null(h.Vm.ErrorMessage);
        Assert.Equal(pools, h.Vm.StorageUsages);
        // Not configured yet: the setup form preselects the first pool.
        Assert.Null(h.Vm.HomesPoolPath);
        Assert.Equal("/data/storage/pool01", h.Vm.HomesSelectedPool);
        // Unrelated settings (language, network, memory) are not read.
        h.Config.Verify(c => c.GetStringAsync("app.language", It.IsAny<string>()), Times.Never);
        systemInfo.Verify(s => s.GetMemoryUsage(), Times.Never);
    }

    [Fact]
    public async Task LoadHomeFolderSettingsAsync_WithoutSettingsPermission_LoadsNothing()
    {
        var systemInfo = new Mock<ISystemInfoService>();
        var h = new Harness(canManageDataServices: true, systemInfo: systemInfo.Object);

        await h.Vm.LoadHomeFolderSettingsAsync();

        Assert.False(h.Vm.CanManageSettings);
        Assert.False(h.Vm.IsLoading);
        systemInfo.Verify(s => s.GetStorageUsage(), Times.Never);
    }
}

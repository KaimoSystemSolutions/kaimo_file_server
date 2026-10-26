using System.Globalization;
using Kaimo_File_Server.Infrastructure.Configuration;
using Kaimo_File_Server.Web.Services;
using Moq;
using Xunit;

namespace Kaimo_File_Server.Tests;

/// <summary>
/// The Region tab has a single save button that persists language and date/time
/// format together (the page then reloads so the new culture/format applies everywhere).
/// </summary>
public partial class SettingsViewModelSmbTests
{
    [Fact]
    public async Task SaveRegionAsync_PersistsLanguageDateAndTimeFormat()
    {
        var h = new Harness(canManageDataServices: false, canManageSettings: true);
        await h.Vm.LoadAsync();
        h.Vm.SelectedLanguage = "en";
        h.Vm.SelectedDateFormat = "american";
        h.Vm.SelectedTimeFormat = "12h";

        var (culture, uiCulture) = (CultureInfo.DefaultThreadCurrentCulture, CultureInfo.DefaultThreadCurrentUICulture);
        try
        {
            var ok = await h.Vm.SaveRegionAsync();

            Assert.True(ok);
            h.Config.Verify(c => c.SetAsync("app.language", "en"), Times.Once);
            h.Config.Verify(c => c.SetAsync(DateFormatService.ConfigKey, "american"), Times.Once);
            h.Config.Verify(c => c.SetAsync(DateFormatService.TimeConfigKey, "12h"), Times.Once);
        }
        finally
        {
            CultureInfo.DefaultThreadCurrentCulture = culture;
            CultureInfo.DefaultThreadCurrentUICulture = uiCulture;
        }
    }

    [Fact]
    public async Task SaveRegionAsync_LanguageFails_SkipsFormatAndKeepsError()
    {
        var h = new Harness(canManageDataServices: false, canManageSettings: true);
        await h.Vm.LoadAsync();
        h.Config.Setup(c => c.SetAsync("app.language", It.IsAny<string>())).ThrowsAsync(new IOException("db down"));

        var ok = await h.Vm.SaveRegionAsync();

        Assert.False(ok);
        Assert.NotNull(h.Vm.ErrorMessage);
        h.Config.Verify(c => c.SetAsync(DateFormatService.ConfigKey, It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task SaveRegionAsync_WithoutPermission_IsDenied()
    {
        var h = new Harness(canManageDataServices: true);
        await h.Vm.LoadAsync();

        var ok = await h.Vm.SaveRegionAsync();

        Assert.False(ok);
        h.Config.Verify(c => c.SetAsync(It.IsAny<string>(), It.IsAny<string>()), Times.Never);
    }
}

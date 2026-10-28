using Kaimo_File_Server.Core.Language;
using Kaimo_File_Server.Web.Services;
using Xunit;

namespace Kaimo_File_Server.Tests;

/// <summary>
/// Save feedback on the Settings page is raised as a toast: the in-page banners sit at the top
/// of the scrolling pane (out of view after scrolling down) and are owned by the page, which
/// does not re-render when a tab component saves (the context-menu tab showed no feedback).
/// </summary>
public partial class SettingsViewModelSmbTests
{
    [Fact]
    public async Task SaveContextMenuAsync_Success_ShowsSuccessToast()
    {
        var h = new Harness(canManageDataServices: false, canManageSettings: true);
        await h.Vm.LoadAsync();
        // The harness does not stub every settings section, so the load may raise its own toast.
        foreach (var t in h.Toasts.Toasts.ToList()) h.Toasts.Remove(t.Id);

        var ok = await h.Vm.SaveContextMenuAsync();

        Assert.True(ok);
        var toast = Assert.Single(h.Toasts.Toasts);
        Assert.Equal(ToastType.Success, toast.Type);
        Assert.Equal(Resources.Web_Settings_CtxSaved, toast.Text);
    }

    [Fact]
    public async Task SaveContextMenuAsync_WithoutPermission_ShowsErrorToast()
    {
        var h = new Harness(canManageDataServices: true);
        await h.Vm.LoadAsync();

        var ok = await h.Vm.SaveContextMenuAsync();

        Assert.False(ok);
        var toast = Assert.Single(h.Toasts.Toasts);
        Assert.Equal(ToastType.Error, toast.Type);
        Assert.Equal(Resources.Web_Settings_NoPermissionChange, toast.Text);
    }

    [Fact]
    public async Task LoadAsync_ClearingMessages_ShowsNoToast()
    {
        var h = new Harness(canManageDataServices: true);

        await h.Vm.LoadAsync();

        Assert.Empty(h.Toasts.Toasts);
    }
}

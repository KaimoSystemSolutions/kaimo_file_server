using Bunit;
using Bunit.TestDoubles;
using Kaimo_File_Server.Core.Domain;
using Kaimo_File_Server.Infrastructure.Configuration;
using Kaimo_File_Server.Web.Components.Pages.Acl;
using Kaimo_File_Server.Web.Components.Pages.Files.FileBrowser.components;
using Kaimo_File_Server.Web.Components.ViewModels;
using Kaimo_File_Server.Web.Services;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using Xunit;

namespace Kaimo_File_Server.Tests;

/// <summary>
/// The file browser's "Permissions" command opens the properties dialog on its Security tab
/// instead of an inline editor or a separate popup; a mounted virtual share shows its access
/// list there too. The dialog widens slightly while the Security tab is active.
/// </summary>
public class PropertiesDialogPermissionsTests
{
    private static readonly FileMetadata Folder = new() { Name = "docs", Path = "docs", IsDirectory = true };

    [Fact]
    public async Task OpenPermissions_ShowsSecurityTab_Widened()
    {
        using var ctx = NewContext();
        var cut = Render(ctx, canManageAcls: true);

        await cut.InvokeAsync(() => cut.Instance.OpenPermissions(Folder));

        Assert.Contains("properties-dialog--wide", cut.Find(".properties-dialog").ClassName);
        Assert.Single(cut.FindComponents<Stub<AclEditor>>());
        Assert.Equal(2, cut.FindAll(".properties-tab").Count);
    }

    [Fact]
    public async Task Open_ShowsGeneralTab_NotWidened_UntilSecurityIsPicked()
    {
        using var ctx = NewContext();
        var cut = Render(ctx, canManageAcls: true);

        await cut.InvokeAsync(() => cut.Instance.Open(Folder));
        Assert.DoesNotContain("properties-dialog--wide", cut.Find(".properties-dialog").ClassName);
        Assert.Empty(cut.FindComponents<Stub<AclEditor>>());

        cut.FindAll(".properties-tab")[1].Click();

        Assert.Contains("properties-dialog--wide", cut.Find(".properties-dialog").ClassName);
        Assert.Single(cut.FindComponents<Stub<AclEditor>>());
    }

    [Fact]
    public async Task OpenPermissions_WithoutAclRights_FallsBackToGeneral()
    {
        using var ctx = NewContext();
        var cut = Render(ctx, canManageAcls: false);

        await cut.InvokeAsync(() => cut.Instance.OpenPermissions(Folder));

        Assert.DoesNotContain("properties-dialog--wide", cut.Find(".properties-dialog").ClassName);
        Assert.Single(cut.FindAll(".properties-tab"));
        Assert.Empty(cut.FindComponents<Stub<AclEditor>>());
    }

    [Fact]
    public async Task OpenPermissions_SecurityOnly_ShowsOnlyTheAclEditorForThatPath()
    {
        using var ctx = NewContext();
        var cut = Render(ctx, canManageAcls: true);
        var shareRoot = new FileMetadata { Name = "host", Path = "", IsDirectory = true };

        await cut.InvokeAsync(() => cut.Instance.OpenPermissions(shareRoot, securityOnly: true));

        Assert.Contains("properties-dialog--wide", cut.Find(".properties-dialog").ClassName);
        Assert.Single(cut.FindAll(".properties-tab"));
        var editor = Assert.Single(cut.FindComponents<Stub<AclEditor>>());
        Assert.Equal("", editor.Instance.Parameters.Get(e => e.Path));
    }

    [Fact]
    public async Task Open_AfterSecurityOnly_ShowsTheGeneralTabAgain()
    {
        using var ctx = NewContext();
        var cut = Render(ctx, canManageAcls: true);
        await cut.InvokeAsync(() => cut.Instance.OpenPermissions(Folder, securityOnly: true));

        await cut.InvokeAsync(() => cut.Instance.Open(Folder));

        Assert.Equal(2, cut.FindAll(".properties-tab").Count);
    }

    [Fact]
    public async Task AclEditorChange_IsForwardedToTheHost()
    {
        using var ctx = NewContext();
        var changed = 0;
        var cut = Render(ctx, canManageAcls: true, onAclChanged: () => changed++);
        await cut.InvokeAsync(() => cut.Instance.OpenPermissions(Folder));

        var editor = cut.FindComponent<Stub<AclEditor>>();
        await cut.InvokeAsync(() => editor.Instance.Parameters.Get(e => e.OnChanged).InvokeAsync());

        Assert.Equal(1, changed);
    }

    [Fact]
    public async Task OpenMountPermissions_ShowsOnlyTheAccessList()
    {
        using var ctx = NewContext();
        var cut = Render(ctx, canManageAcls: false);
        var mount = new MountedFolder(Guid.NewGuid(), "host", "docs") { CanManage = true };

        await cut.InvokeAsync(() => cut.Instance.OpenMountPermissions(mount, Folder));

        Assert.Contains("properties-dialog--wide", cut.Find(".properties-dialog").ClassName);
        Assert.Single(cut.FindAll(".properties-tab"));
        var editor = Assert.Single(cut.FindComponents<Stub<MountGrantsEditor>>());
        Assert.Equal(mount, editor.Instance.Parameters.Get(e => e.Mount));
        Assert.Empty(cut.FindComponents<Stub<AclEditor>>());
    }

    [Fact]
    public async Task OpenVirtualSharePermissions_ShowsOnlyThatSharesAccessList()
    {
        using var ctx = NewContext();
        var cut = Render(ctx, canManageAcls: false);
        var shareId = Guid.NewGuid();

        await cut.InvokeAsync(() => cut.Instance.OpenVirtualSharePermissions(shareId, "cloud"));

        Assert.Contains("properties-dialog--wide", cut.Find(".properties-dialog").ClassName);
        Assert.Equal("cloud", cut.Find(".properties-header-name").TextContent);
        Assert.Single(cut.FindAll(".properties-tab"));
        var editor = Assert.Single(cut.FindComponents<Stub<MountGrantsEditor>>());
        Assert.Equal(shareId, editor.Instance.Parameters.Get(e => e.ShareId));
        Assert.Throws<ParameterNotFoundException>(() => editor.Instance.Parameters.Get(e => e.Mount));
    }

    [Fact]
    public async Task Open_AfterVirtualSharePermissions_ShowsTheRegularDialogAgain()
    {
        using var ctx = NewContext();
        var cut = Render(ctx, canManageAcls: true);
        await cut.InvokeAsync(() => cut.Instance.OpenVirtualSharePermissions(Guid.NewGuid(), "cloud"));

        await cut.InvokeAsync(() => cut.Instance.Open(Folder));

        Assert.Equal(2, cut.FindAll(".properties-tab").Count);
        Assert.Empty(cut.FindComponents<Stub<MountGrantsEditor>>());
    }

    [Fact]
    public async Task Open_AfterMountPermissions_ShowsTheRegularDialogAgain()
    {
        using var ctx = NewContext();
        var cut = Render(ctx, canManageAcls: true);
        await cut.InvokeAsync(() => cut.Instance.OpenMountPermissions(new MountedFolder(Guid.NewGuid(), "host", "docs"), Folder));

        await cut.InvokeAsync(() => cut.Instance.Open(Folder));

        Assert.Equal(2, cut.FindAll(".properties-tab").Count);
        Assert.Empty(cut.FindComponents<Stub<MountGrantsEditor>>());
    }

    [Fact]
    public async Task SmoothResize_IsAttachedOncePerOpen_NotPerTabSwitch()
    {
        using var ctx = NewContext();
        var cut = Render(ctx, canManageAcls: true);

        await cut.InvokeAsync(() => cut.Instance.Open(Folder));
        cut.FindAll(".properties-tab")[1].Click();
        cut.FindAll(".properties-tab")[0].Click();
        Assert.Single(ctx.JSInterop.Invocations["kaimoSmoothResize.observe"]);

        await cut.InvokeAsync(cut.Instance.Close);
        await cut.InvokeAsync(() => cut.Instance.OpenPermissions(Folder));
        Assert.Equal(2, ctx.JSInterop.Invocations["kaimoSmoothResize.observe"].Count);
    }

    [Fact]
    public async Task TabSwitch_RecreatesThePane_SoItsFadeInReplays()
    {
        using var ctx = NewContext();
        var cut = Render(ctx, canManageAcls: true);
        await cut.InvokeAsync(() => cut.Instance.Open(Folder));
        var generalPane = cut.Find(".properties-pane");

        cut.FindAll(".properties-tab")[1].Click();

        Assert.NotSame(generalPane, cut.Find(".properties-pane"));
    }

    private static IRenderedComponent<PropertiesDialog> Render(BunitContext ctx, bool canManageAcls, Action? onAclChanged = null)
    {
        var vm = new Mock<IFileBrowserViewModel> { CallBase = true };
        vm.SetupGet(v => v.CanManageAcls).Returns(canManageAcls);
        vm.SetupGet(v => v.CurrentShare).Returns(new ShareDefinition("host", "/mnt/host") { Id = Guid.NewGuid() });
        vm.Setup(v => v.ShareRelativeOf(It.IsAny<FileMetadata>())).Returns<FileMetadata>(f => f.Path);
        return ctx.Render<PropertiesDialog>(p =>
        {
            p.Add(d => d.VM, vm.Object);
            if (onAclChanged is not null) p.Add(d => d.OnAclChanged, onAclChanged);
        });
    }

    private static BunitContext NewContext()
    {
        var ctx = new BunitContext();
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        var env = new Mock<Microsoft.AspNetCore.Hosting.IWebHostEnvironment>();
        env.SetupGet(e => e.WebRootPath).Returns(MailNotificationTests.FindWebRoot());
        ctx.Services.AddSingleton(new AssetProvider(env.Object));
        ctx.Services.AddSingleton(new DateFormatService(Mock.Of<IConfigRepository>()));
        ctx.ComponentFactories.AddStub<AclEditor>();
        ctx.ComponentFactories.AddStub<MountGrantsEditor>();
        return ctx;
    }
}

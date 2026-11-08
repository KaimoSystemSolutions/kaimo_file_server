using Bunit;
using Kaimo_File_Server.Web.Components.Pages.CloudSync.Components;
using Kaimo_File_Server.Web.Components.ViewModels;
using Kaimo_File_Server.Web.Services;
using Kaimo_File_Server.Web.Tests.Services;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using Xunit;

namespace Kaimo_File_Server.Web.Tests.Components;

public sealed class DirectoryPickerDialogTests : IDisposable
{
    private readonly BunitContext _ctx = new();

    public DirectoryPickerDialogTests()
    {
        var env = new Mock<Microsoft.AspNetCore.Hosting.IWebHostEnvironment>();
        env.SetupGet(e => e.WebRootPath).Returns(MailNotificationTests.FindWebRoot());
        _ctx.Services.AddSingleton(new AssetProvider(env.Object));
    }

    public void Dispose() => _ctx.Dispose();

    [Fact]
    public void Select_IsAvailableByDefault()
    {
        var cut = Render();

        Assert.False(cut.Find(".modal-actions .btn-primary").HasAttribute("disabled"));
    }

    [Fact]
    public void Select_IsGreyedOutButNavigationAndMessageRemain()
    {
        var cut = Render(canSelect: false, error: "This folder cannot be synchronized");

        Assert.True(cut.Find(".modal-actions .btn-primary").HasAttribute("disabled"));
        Assert.False(cut.Find(".directory-picker-item").HasAttribute("disabled"));
        Assert.Contains("cannot be synchronized", cut.Find(".modal-error").TextContent);
    }

    private IRenderedComponent<DirectoryPickerDialog> Render(bool canSelect = true, string? error = null)
        => _ctx.Render<DirectoryPickerDialog>(parameters => parameters
            .Add(dialog => dialog.IsOpen, true)
            .Add(dialog => dialog.IsRemote, true)
            .Add(dialog => dialog.CurrentPath, "/data")
            .Add(dialog => dialog.CanSelect, canSelect)
            .Add(dialog => dialog.ErrorMessage, error)
            .Add(dialog => dialog.Items, new[] { new CloudDirectoryItem("child", "/data/child") }));
}

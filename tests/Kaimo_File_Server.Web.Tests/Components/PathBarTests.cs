using Bunit;
using Kaimo_File_Server.Web.Components.Shared;
using Microsoft.AspNetCore.Components;
using Xunit;

namespace Kaimo_File_Server.Web.Tests.Components;

/// <summary>
/// Editable path bar: the free space after the crumbs opens a text field; Enter navigates to a
/// changed path, Escape closes without navigating, and a busy host never gets a second request.
/// </summary>
public sealed class PathBarTests : IDisposable
{
    private readonly BunitContext _ctx = new();
    private readonly List<string> _navigated = [];

    public PathBarTests() => _ctx.JSInterop.Mode = JSRuntimeMode.Loose;

    public void Dispose() => _ctx.Dispose();

    [Fact]
    public void Enter_NavigatesToTypedPath()
    {
        var cut = Render(path: "a");
        Edit(cut, "Docs/a/b");

        cut.Find("input.path-bar-input").KeyDown("Enter");

        Assert.Equal(["a/b"], _navigated);
        Assert.Empty(cut.FindAll("input.path-bar-input"));
    }

    [Fact]
    public void Enter_UnchangedPath_DoesNotNavigate()
    {
        var cut = Render(path: "a");
        Edit(cut, "Docs/a");

        cut.Find("input.path-bar-input").KeyDown("Enter");

        Assert.Empty(_navigated);
    }

    [Fact]
    public void Escape_ClosesWithoutNavigating()
    {
        var cut = Render(path: "a");
        Edit(cut, "Docs/x");

        cut.Find("input.path-bar-input").KeyDown("Escape");

        Assert.Empty(_navigated);
        Assert.NotNull(cut.Find("nav.path-bar"));
    }

    [Fact]
    public void Enter_WhileHostBecameBusy_DoesNotNavigate()
    {
        var cut = Render(path: "a");
        Edit(cut, "Docs/x");
        cut.Render(parameters => parameters.Add(bar => bar.Disabled, true));

        cut.Find("input.path-bar-input").KeyDown("Enter");

        Assert.Empty(_navigated);
        Assert.NotNull(cut.Find("input.path-bar-input"));
    }

    [Fact]
    public void EditButton_IsDisabledWhileBusy()
    {
        var cut = Render(path: "", disabled: true);

        Assert.True(cut.Find("button.path-bar-edit").HasAttribute("disabled"));
    }

    private IRenderedComponent<PathBar> Render(string path, bool disabled = false)
        => _ctx.Render<PathBar>(parameters => parameters
            .Add(bar => bar.RootName, "Docs")
            .Add(bar => bar.Path, path)
            .Add(bar => bar.Disabled, disabled)
            .Add(bar => bar.OnNavigate, EventCallback.Factory.Create<string>(this, p => _navigated.Add(p))));

    private static void Edit(IRenderedComponent<PathBar> cut, string text)
    {
        cut.Find("button.path-bar-edit").Click();
        cut.Find("input.path-bar-input").Input(text);
    }
}

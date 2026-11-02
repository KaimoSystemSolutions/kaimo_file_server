using Kaimo_File_Server.Web.Components.ViewModels;
using Kaimo_File_Server.Web.Services;
using Xunit;

namespace Kaimo_File_Server.Tests;

/// <summary>
/// The clipboard is shared by every file browser of a circuit. Its generation tells a
/// finished paste whether the clipboard it pasted is still the current one, even when the
/// paste ran in a browser page the user has left meanwhile.
/// </summary>
public sealed class FileBrowserClipboardServiceTests
{
    private static readonly BrowserShareInfo Share = new(Guid.NewGuid(), "share", BrowserShareKind.Local);

    [Fact]
    public void Set_ChangesTheGeneration()
    {
        var sut = new FileBrowserClipboardService();
        var before = sut.Generation;

        sut.Set(Share, [], deleteOnPaste: false, startPath: "");

        Assert.NotEqual(before, sut.Generation);
    }

    [Fact]
    public void Clear_ChangesTheGeneration()
    {
        var sut = new FileBrowserClipboardService();
        sut.Set(Share, [], deleteOnPaste: false, startPath: "");
        var before = sut.Generation;

        sut.Clear();

        Assert.NotEqual(before, sut.Generation);
    }

    [Fact]
    public void ToastIdChanges_KeepTheGeneration()
    {
        var sut = new FileBrowserClipboardService();
        sut.Set(Share, [], deleteOnPaste: false, startPath: "");
        var before = sut.Generation;

        sut.SetToastId("toast");
        sut.ClearToastId();

        Assert.Equal(before, sut.Generation);
    }

    // ═══════════════════ One paste per cut ═══════════════════

    [Fact]
    public void TryBeginCutPaste_RefusesTheSameCut_WhileItIsBeingPasted()
    {
        var sut = new FileBrowserClipboardService();
        sut.Set(Share, [], deleteOnPaste: true, startPath: "");

        Assert.True(sut.TryBeginCutPaste(sut.Generation));
        Assert.False(sut.TryBeginCutPaste(sut.Generation));
    }

    [Fact]
    public void TryBeginCutPaste_AllowsTheCutAgain_AfterThePasteEnded()
    {
        var sut = new FileBrowserClipboardService();
        sut.Set(Share, [], deleteOnPaste: true, startPath: "");
        var generation = sut.Generation;
        sut.TryBeginCutPaste(generation);

        sut.EndCutPaste(generation);

        Assert.True(sut.TryBeginCutPaste(generation));
    }

    [Fact]
    public void TryBeginCutPaste_AllowsANewCut_WhileAnEarlierOneIsBeingPasted()
    {
        var sut = new FileBrowserClipboardService();
        sut.Set(Share, [], deleteOnPaste: true, startPath: "");
        var first = sut.Generation;
        sut.TryBeginCutPaste(first);

        sut.Set(Share, [], deleteOnPaste: true, startPath: "");

        Assert.True(sut.TryBeginCutPaste(sut.Generation));
        sut.EndCutPaste(first);
        Assert.False(sut.TryBeginCutPaste(sut.Generation)); // ending the first leaves the second running
    }
}

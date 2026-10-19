using Kaimo_File_Server.Web.Components.ViewModels;
using Xunit;

namespace Kaimo_File_Server.Tests;

/// <summary>
/// Locks the demo read-only capability projection: every write action must be off,
/// every view/read feature must stay on so the demo can still show everything.
/// </summary>
public sealed class BrowserCapabilitiesReadOnlyTests
{
    private readonly BrowserCapabilities _ro = BrowserCapabilities.Local.AsReadOnly();

    [Fact]
    public void Write_actions_are_all_disabled()
    {
        Assert.False(_ro.CanUpload);
        Assert.False(_ro.CanCreateDirectory);
        Assert.False(_ro.CanRename);
        Assert.False(_ro.CanDelete);
        Assert.False(_ro.CanMove);
        Assert.False(_ro.CanCopy);
        Assert.False(_ro.CanCut);
        Assert.False(_ro.CanArchive);
        Assert.False(_ro.CanExtract);
    }

    [Fact]
    public void View_features_stay_enabled()
    {
        Assert.True(_ro.CanOpen);
        Assert.True(_ro.HasFileAcls);
        Assert.True(_ro.HasVersions);
        Assert.True(_ro.HasProperties);
        Assert.True(_ro.HasSearchIntegration);
        Assert.True(_ro.ShowDirectorySizes);
    }
}

using Kaimo_File_Server.Web.Components.ViewModels;
using Xunit;

namespace Kaimo_File_Server.Tests;

/// <summary>
/// Share identity is the id plus the backend kind; display fields (name, provider) do not
/// tell two shares apart, so a rename or a reload never looks like a share change.
/// </summary>
public sealed class BrowserShareInfoTests
{
    private static readonly Guid Id = Guid.NewGuid();

    [Fact]
    public void SameIdAndKind_IsTheSameShare()
        => Assert.True(new BrowserShareInfo(Id, "a", BrowserShareKind.Remote, "p1")
            .IsSameShareAs(new BrowserShareInfo(Id, "b", BrowserShareKind.Remote, "p2")));

    [Fact]
    public void OtherIdOrKind_IsAnotherShare()
    {
        var share = new BrowserShareInfo(Id, "a", BrowserShareKind.Local);

        Assert.False(share.IsSameShareAs(share with { Id = Guid.NewGuid() }));
        Assert.False(share.IsSameShareAs(share with { Kind = BrowserShareKind.Remote }));
        Assert.False(share.IsSameShareAs(null));
    }
}

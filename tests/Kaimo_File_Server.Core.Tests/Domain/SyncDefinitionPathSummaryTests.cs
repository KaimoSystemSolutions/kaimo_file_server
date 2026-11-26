using Kaimo_File_Server.Core.Domain;
using Kaimo_File_Server.Core.Services.CloudSync;
using Xunit;

namespace Kaimo_File_Server.Core.Tests.Domain;

/// <summary>
/// The Running Jobs menu shows this summary under each sync job; the arrow must
/// reflect the configured direction instead of always showing both ways.
/// </summary>
public class SyncDefinitionPathSummaryTests
{
    [Theory]
    [InlineData(SyncMode.Pull, "docs ← /remote")]
    [InlineData(SyncMode.Push, "docs → /remote")]
    [InlineData(SyncMode.TwoWay, "docs ↔ /remote")]
    public void DescribePaths_UsesArrowForMode(SyncMode mode, string expected)
    {
        var definition = new SyncDefinition { LocalPath = "docs", RemotePath = "/remote", Mode = mode };

        Assert.Equal(expected, definition.DescribePaths());
    }

    [Fact]
    public void DescribePaths_ShowsTheGivenLocalPathInPlaceOfTheStoredOne()
    {
        // A home on the home-folder share is shown by its owner's username; the stored path keeps the GUID.
        var home = Guid.NewGuid().ToString("D");
        var definition = new SyncDefinition { LocalPath = $"{home}/docs", RemotePath = "/remote", Mode = SyncMode.Push };

        Assert.Equal("bob/docs → /remote", definition.DescribePaths("bob/docs"));
        Assert.Equal($"{home}/docs", definition.LocalPath);
    }
}

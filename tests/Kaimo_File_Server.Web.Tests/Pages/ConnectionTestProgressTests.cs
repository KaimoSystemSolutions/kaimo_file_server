using System.Xml.Linq;
using Kaimo_File_Server.Web.Components.Pages.ExternalStorage.Components;
using Xunit;

namespace Kaimo_File_Server.Web.Tests.Pages;

/// <summary>
/// The connection test cannot measure real progress, so the bar shows the share of the
/// time limit already used and the label how many seconds the test may still take at most.
/// </summary>
public sealed class ConnectionTestProgressTests
{
    private static readonly TimeSpan Limit = TimeSpan.FromSeconds(60);

    [Theory]
    [InlineData(0, 0)]
    [InlineData(15, 25)]
    [InlineData(30, 50)]
    [InlineData(59.9, 99)]
    [InlineData(60, 100)]
    [InlineData(90, 100)]
    public void Percent_IsShareOfLimitClampedTo100(double elapsedSeconds, int expected)
        => Assert.Equal(expected, ConnectionTestProgress.Percent(TimeSpan.FromSeconds(elapsedSeconds), Limit));

    [Fact]
    public void Percent_NegativeElapsed_IsZero()
        => Assert.Equal(0, ConnectionTestProgress.Percent(TimeSpan.FromSeconds(-1), Limit));

    [Fact]
    public void Percent_ZeroLimit_IsFull()
        => Assert.Equal(100, ConnectionTestProgress.Percent(TimeSpan.Zero, TimeSpan.Zero));

    [Theory]
    [InlineData(0, 60)]
    [InlineData(0.4, 60)]
    [InlineData(1, 59)]
    [InlineData(59.5, 1)]
    [InlineData(60, 0)]
    [InlineData(75, 0)]
    public void RemainingSeconds_RoundsUpAndNeverGoesNegative(double elapsedSeconds, int expected)
        => Assert.Equal(expected, ConnectionTestProgress.RemainingSeconds(TimeSpan.FromSeconds(elapsedSeconds), Limit));

    [Theory]
    [InlineData("Resources.resx")]
    [InlineData("Resources.de.resx")]
    public void ProgressAndTimeoutTexts_ExistInLanguageFile(string resxFile)
    {
        var defined = XDocument.Load(Path.Combine(FindRepoRoot(), "src/Kaimo_File_Server.Core/Language", resxFile))
            .Root!.Elements("data")
            .Where(e => !string.IsNullOrWhiteSpace(e.Element("value")?.Value))
            .ToDictionary(e => (string)e.Attribute("name")!, e => e.Element("value")!.Value);

        Assert.Contains("{0}", defined["Web_ExternalStorage_TestRunning"]);
        Assert.True(defined.ContainsKey("Web_ExternalStorage_ErrorCode_connection_test_timeout"));
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Kaimo_File_Server.slnx")))
            dir = dir.Parent;
        return dir?.FullName ?? throw new DirectoryNotFoundException("Repository root not found.");
    }
}

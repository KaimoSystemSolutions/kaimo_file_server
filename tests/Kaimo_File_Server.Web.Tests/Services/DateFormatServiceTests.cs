using Kaimo_File_Server.Web.Services;
using Xunit;

namespace Kaimo_File_Server.Web.Tests.Services;

/// <summary>
/// The global date-display format maps each configured key to explicit date /
/// date-time patterns, and any unknown/blank value falls back to ISO (the default).
/// </summary>
public class DateFormatServiceTests
{
    [Theory]
    [InlineData("iso", "yyyy-MM-dd", "yyyy-MM-dd HH:mm")]
    [InlineData("american", "MM/dd/yyyy", "MM/dd/yyyy HH:mm")]
    [InlineData("european", "dd.MM.yyyy", "dd.MM.yyyy HH:mm")]
    [InlineData("EUROPEAN", "dd.MM.yyyy", "dd.MM.yyyy HH:mm")] // case-insensitive
    [InlineData("nonsense", "yyyy-MM-dd", "yyyy-MM-dd HH:mm")] // unknown → default
    [InlineData("", "yyyy-MM-dd", "yyyy-MM-dd HH:mm")]
    public void PatternFor_MapsKnownFormats_AndFallsBackToIso(
        string format, string expectedDate, string expectedDateTime)
    {
        var (date, dateTime) = DateFormatService.PatternFor(format);
        Assert.Equal(expectedDate, date);
        Assert.Equal(expectedDateTime, dateTime);
    }

    [Theory]
    [InlineData("24h", "HH:mm")]
    [InlineData("12h", "h:mm tt")]
    [InlineData("12H", "h:mm tt")] // case-insensitive
    [InlineData("nonsense", "HH:mm")] // unknown → default (24h)
    [InlineData("", "HH:mm")]
    public void TimePatternFor_MapsKnownFormats_AndFallsBackTo24h(string timeFormat, string expected)
        => Assert.Equal(expected, DateFormatService.TimePatternFor(timeFormat));

    // The pattern getters never touch the config repository, so no stub is needed.
    private static DateFormatService Service() => new(null!);

    [Fact]
    public void Format_UnixEpoch_IsUnknown_AndRendersNullText()
    {
        var service = Service();
        Assert.Equal("–", service.FormatDateTime(DateTime.UnixEpoch));
        Assert.Equal("–", service.FormatDate(DateTime.UnixEpoch));
        Assert.Equal("n/a", service.FormatDateTime(DateTime.UnixEpoch, "n/a"));
    }

    [Fact]
    public void Format_Null_RendersNullText()
    {
        var service = Service();
        Assert.Equal("–", service.FormatDateTime(null));
        Assert.Equal("–", service.FormatDate(null));
    }

    [Fact]
    public void Format_RealTimestamp_IsStillRendered_EvenOneSecondAfterEpoch()
    {
        var service = Service();
        var justAfterEpoch = DateTime.UnixEpoch.AddSeconds(1);
        Assert.Equal(
            justAfterEpoch.ToLocalTime().ToString("yyyy-MM-dd HH:mm"),
            service.FormatDateTime(justAfterEpoch));
        Assert.Equal(
            justAfterEpoch.ToLocalTime().ToString("yyyy-MM-dd"),
            service.FormatDate(justAfterEpoch));
    }
}

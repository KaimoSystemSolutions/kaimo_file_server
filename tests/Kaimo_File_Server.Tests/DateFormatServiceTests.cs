using Kaimo_File_Server.Web.Services;
using Xunit;

namespace Kaimo_File_Server.Tests;

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
}

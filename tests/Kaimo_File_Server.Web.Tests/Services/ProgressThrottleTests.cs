using Kaimo_File_Server.Web.Services;
using Xunit;

namespace Kaimo_File_Server.Web.Tests.Services;

/// <summary>
/// Per-item progress reports of long loops (delete, paste, empty recycle bin) are limited to
/// a few per second, so their re-renders do not crowd out browsing on the same circuit.
/// </summary>
public sealed class ProgressThrottleTests
{
    private readonly MutableTimeProvider _time = new();
    private readonly ProgressThrottle _sut;

    public ProgressThrottleTests() => _sut = new ProgressThrottle(_time);

    [Fact]
    public void FirstReport_IsDue()
        => Assert.True(_sut.ShouldReport());

    [Fact]
    public void ReportWithinTheInterval_IsSkipped()
    {
        _sut.ShouldReport();
        _time.Advance(ProgressThrottle.DefaultInterval - TimeSpan.FromMilliseconds(1));

        Assert.False(_sut.ShouldReport());
    }

    [Fact]
    public void ReportAfterTheInterval_IsDue_AndRestartsTheInterval()
    {
        _sut.ShouldReport();
        _time.Advance(ProgressThrottle.DefaultInterval);

        Assert.True(_sut.ShouldReport());
        Assert.False(_sut.ShouldReport());
    }

    [Fact]
    public void FinalReport_IsAlwaysDue()
    {
        _sut.ShouldReport();

        Assert.True(_sut.ShouldReport(isFinal: true));
    }
}

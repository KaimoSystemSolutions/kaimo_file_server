namespace Kaimo_File_Server.Web.Services;

/// <summary>
/// Limits the progress reports of a long per-item loop to a few per second. Every report
/// re-renders the toast and the job menu, and on Blazor Server those renders share the
/// circuit with the browsing the user does meanwhile.
/// </summary>
public sealed class ProgressThrottle(TimeProvider? time = null)
{
    public static readonly TimeSpan DefaultInterval = TimeSpan.FromMilliseconds(250);

    private readonly TimeProvider _time = time ?? TimeProvider.System;
    private DateTimeOffset? _lastReport;

    /// <summary>True for the first report, the final one and any after the interval.</summary>
    public bool ShouldReport(bool isFinal = false)
    {
        var now = _time.GetUtcNow();
        if (!isFinal && _lastReport is { } last && now - last < DefaultInterval)
            return false;
        _lastReport = now;
        return true;
    }
}

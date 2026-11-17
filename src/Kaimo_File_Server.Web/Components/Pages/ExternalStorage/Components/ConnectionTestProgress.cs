namespace Kaimo_File_Server.Web.Components.Pages.ExternalStorage.Components;

/// <summary>
/// A connection test is a single remote round trip without measurable progress, so the
/// progress bar shows how much of the test's time limit is used up and the label the
/// longest time the test may still take.
/// </summary>
public static class ConnectionTestProgress
{
    /// <summary>Share of <paramref name="limit"/> already elapsed, 0–100.</summary>
    public static int Percent(TimeSpan elapsed, TimeSpan limit)
    {
        if (limit <= TimeSpan.Zero)
            return 100;
        double share = elapsed.TotalMilliseconds / limit.TotalMilliseconds * 100;
        return (int)Math.Clamp(Math.Floor(share), 0, 100);
    }

    /// <summary>Whole seconds left until the limit, rounded up; never negative.</summary>
    public static int RemainingSeconds(TimeSpan elapsed, TimeSpan limit)
    {
        TimeSpan remaining = limit - elapsed;
        return remaining <= TimeSpan.Zero ? 0 : (int)Math.Ceiling(remaining.TotalSeconds);
    }
}

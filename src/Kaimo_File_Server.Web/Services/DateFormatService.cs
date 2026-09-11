using System.Globalization;
using Kaimo_File_Server.Infrastructure.Configuration;

namespace Kaimo_File_Server.Web.Services;

/// <summary>
/// Centralizes how dates/timestamps are rendered across the web UI.
/// The format is a global setting (config key <see cref="ConfigKey"/>) chosen by an
/// administrator; this scoped service loads it once per circuit, mirroring
/// <see cref="ThemeService"/>. Replaces the previously duplicated inline formatting.
/// The date-format setting controls the date part; a separate time-format setting
/// (<see cref="TimeConfigKey"/>) selects 24-hour or 12-hour (AM/PM) time.
/// </summary>
public class DateFormatService
{
    public const string ConfigKey = "display.dateformat";
    public const string DefaultFormat = "iso";
    public const string TimeConfigKey = "display.timeformat";
    public const string DefaultTimeFormat = "24h";

    private readonly IConfigRepository _config;
    private string _format = DefaultFormat;
    private string _timeFormat = DefaultTimeFormat;
    private bool _loaded;

    public DateFormatService(IConfigRepository config) => _config = config;

    public string Current => _format;

    public string CurrentTime => _timeFormat;

    /// <summary>Date-only pattern for the active format (explicit, culture-invariant).</summary>
    public string DatePattern => PatternFor(_format).date;

    /// <summary>Time-only pattern for the active time format (24h or 12h AM/PM).</summary>
    public string TimePattern => TimePatternFor(_timeFormat);

    /// <summary>Date + time pattern combining the active date and time formats.</summary>
    public string DateTimePattern => $"{DatePattern} {TimePattern}";

    /// <summary>Loads the configured formats once. Idempotent; safe to call repeatedly.</summary>
    public async Task InitializeAsync()
    {
        if (_loaded)
            return;
        try
        {
            _format = Normalize(await _config.GetStringAsync(ConfigKey, DefaultFormat));
            _timeFormat = NormalizeTime(await _config.GetStringAsync(TimeConfigKey, DefaultTimeFormat));
        }
        catch
        {
            // Config unavailable → keep the defaults (current behavior).
        }
        _loaded = true;
    }

    /// <summary>Formats a UTC timestamp as local date + time, or <paramref name="nullText"/> when null.</summary>
    public string FormatDateTime(DateTime? utc, string nullText = "–")
        => utc.HasValue
            ? utc.Value.ToLocalTime().ToString(DateTimePattern, CultureInfo.InvariantCulture)
            : nullText;

    /// <summary>Formats a UTC timestamp as a local date (no time), or <paramref name="nullText"/> when null.</summary>
    public string FormatDate(DateTime? utc, string nullText = "–")
        => utc.HasValue
            ? utc.Value.ToLocalTime().ToString(DatePattern, CultureInfo.InvariantCulture)
            : nullText;

    /// <summary>Formats an already-local value as date + time, without re-converting the time zone.</summary>
    public string FormatDateTimeLocal(DateTime local)
        => local.ToString(DateTimePattern, CultureInfo.InvariantCulture);

    private static string Normalize(string? value)
    {
        var v = value?.Trim().ToLowerInvariant();
        return v is "iso" or "american" or "european" ? v : DefaultFormat;
    }

    private static string NormalizeTime(string? value)
    {
        var v = value?.Trim().ToLowerInvariant();
        return v is "12h" or "24h" ? v : DefaultTimeFormat;
    }

    /// <summary>Maps a date-format key to its date / (24h) date-time patterns. Public for the settings preview.</summary>
    public static (string date, string dateTime) PatternFor(string format) => Normalize(format) switch
    {
        "european" => ("dd.MM.yyyy", "dd.MM.yyyy HH:mm"),
        "american" => ("MM/dd/yyyy", "MM/dd/yyyy HH:mm"),
        _ => ("yyyy-MM-dd", "yyyy-MM-dd HH:mm"), // iso (default)
    };

    /// <summary>Maps a time-format key to its explicit, culture-invariant pattern. Public for the settings preview.</summary>
    public static string TimePatternFor(string timeFormat)
        => NormalizeTime(timeFormat) == "12h" ? "h:mm tt" : "HH:mm";
}

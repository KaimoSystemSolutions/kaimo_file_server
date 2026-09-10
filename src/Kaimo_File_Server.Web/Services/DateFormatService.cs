using System.Globalization;
using Kaimo_File_Server.Infrastructure.Configuration;

namespace Kaimo_File_Server.Web.Services;

/// <summary>
/// Centralizes how dates/timestamps are rendered across the web UI.
/// The format is a global setting (config key <see cref="ConfigKey"/>) chosen by an
/// administrator; this scoped service loads it once per circuit, mirroring
/// <see cref="ThemeService"/>. Replaces the previously duplicated inline formatting.
/// The setting only controls the date part; the time stays 24h <c>HH:mm</c>.
/// </summary>
public class DateFormatService
{
    public const string ConfigKey = "display.dateformat";
    public const string DefaultFormat = "iso";

    private readonly IConfigRepository _config;
    private string _format = DefaultFormat;
    private bool _loaded;

    public DateFormatService(IConfigRepository config) => _config = config;

    public string Current => _format;

    /// <summary>Date-only pattern for the active format (explicit, culture-invariant).</summary>
    public string DatePattern => PatternFor(_format).date;

    /// <summary>Date + 24h time pattern for the active format.</summary>
    public string DateTimePattern => PatternFor(_format).dateTime;

    /// <summary>Loads the configured format once. Idempotent; safe to call repeatedly.</summary>
    public async Task InitializeAsync()
    {
        if (_loaded)
            return;
        try
        {
            _format = Normalize(await _config.GetStringAsync(ConfigKey, DefaultFormat));
        }
        catch
        {
            // Config unavailable → keep the default (current behavior).
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

    /// <summary>Maps a format key to its date / date-time patterns. Public for the settings preview.</summary>
    public static (string date, string dateTime) PatternFor(string format) => Normalize(format) switch
    {
        "european" => ("dd.MM.yyyy", "dd.MM.yyyy HH:mm"),
        "american" => ("MM/dd/yyyy", "MM/dd/yyyy HH:mm"),
        _ => ("yyyy-MM-dd", "yyyy-MM-dd HH:mm"), // iso (default)
    };
}

using System.Globalization;
using System.Text.RegularExpressions;

namespace Kaimo_File_Server.Web.DynamicHelpers;

/// <summary>
/// Resolves the display color for a department. If a custom <c>#RRGGBB</c> hex
/// is set it is used verbatim; otherwise a stable color is derived from the
/// department id so every department is visually distinguishable without manual
/// assignment. Returned values are safe to emit into inline CSS (custom input is
/// validated in <see cref="Normalize"/>).
/// </summary>
public static class DepartmentPalette
{
    private static readonly Regex HexPattern = new("^#[0-9A-Fa-f]{6}$", RegexOptions.Compiled);

    // Fixed saturation/lightness keep automatic colors legible in both themes;
    // only the hue varies per department.
    private const int Saturation = 60;
    private const int Lightness = 55;
    private const double SoftAlpha = 0.17;

    /// <summary>Stroke/text color and matching soft background for a department.</summary>
    public static (string Color, string Soft) For(Guid id, string? hex)
    {
        var custom = Normalize(hex);
        if (custom is not null)
        {
            var (r, g, b) = ParseHex(custom);
            return (custom, $"rgba({r}, {g}, {b}, {SoftAlpha.ToString(CultureInfo.InvariantCulture)})");
        }

        var hue = Hue(id);
        return (
            $"hsl({hue}, {Saturation}%, {Lightness}%)",
            $"hsla({hue}, {Saturation}%, {Lightness}%, {SoftAlpha.ToString(CultureInfo.InvariantCulture)})");
    }

    /// <summary>The automatic color as a <c>#RRGGBB</c> hex, used to seed the color picker.</summary>
    public static string AutoHex(Guid id)
    {
        var (r, g, b) = HslToRgb(Hue(id), Saturation / 100.0, Lightness / 100.0);
        return $"#{r:X2}{g:X2}{b:X2}";
    }

    /// <summary>Returns a normalized <c>#RRGGBB</c> hex, or <c>null</c> when the input is not a valid hex color.</summary>
    public static string? Normalize(string? hex)
    {
        if (string.IsNullOrWhiteSpace(hex)) return null;
        hex = hex.Trim();
        return HexPattern.IsMatch(hex) ? hex.ToUpperInvariant() : null;
    }

    // Deterministic hue [0,360) from a stable hash of the id bytes (not GetHashCode).
    private static int Hue(Guid id)
    {
        var sum = 0;
        foreach (var b in id.ToByteArray()) sum += b;
        return sum % 360;
    }

    private static (int R, int G, int B) ParseHex(string hex) => (
        int.Parse(hex.Substring(1, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture),
        int.Parse(hex.Substring(3, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture),
        int.Parse(hex.Substring(5, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture));

    private static (int R, int G, int B) HslToRgb(double h, double s, double l)
    {
        double c = (1 - Math.Abs(2 * l - 1)) * s;
        double x = c * (1 - Math.Abs((h / 60.0) % 2 - 1));
        double m = l - c / 2;
        double r = 0, g = 0, b = 0;
        switch ((int)(h / 60) % 6)
        {
            case 0: r = c; g = x; break;
            case 1: r = x; g = c; break;
            case 2: g = c; b = x; break;
            case 3: g = x; b = c; break;
            case 4: r = x; b = c; break;
            default: r = c; b = x; break;
        }
        return (
            (int)Math.Round((r + m) * 255),
            (int)Math.Round((g + m) * 255),
            (int)Math.Round((b + m) * 255));
    }
}

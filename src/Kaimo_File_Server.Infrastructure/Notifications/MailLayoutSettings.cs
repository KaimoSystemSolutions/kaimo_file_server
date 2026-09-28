using System.Text.RegularExpressions;

namespace Kaimo_File_Server.Infrastructure.Notifications;

/// <summary>
/// The shared frame of every notification mail (logo, accent color, header and footer),
/// stored in <c>ConfigSettings</c> under <see cref="ConfigKey"/>. Header and footer are
/// Liquid templates with the same placeholders as the mail body.
/// </summary>
public sealed partial record MailLayoutSettings
{
    public const string ConfigKey = "notifications.layout";

    /// <summary>Upper bound of the decoded logo image.</summary>
    public const int MaxLogoBytes = 200 * 1024;

    /// <summary>Kaimo green (the default accent token value). Mail clients cannot use CSS variables.</summary>
    public const string DefaultAccentColor = "#3F9E00";

    /// <summary><c>data:image/png|jpeg|gif;base64,…</c>; sent as an inline (CID) attachment.</summary>
    public string? LogoDataUri { get; set; }

    /// <summary><c>#RRGGBB</c> used for the header bar and buttons.</summary>
    public string AccentColor { get; set; } = DefaultAccentColor;

    public string HeaderHtml { get; set; } = string.Empty;
    public string FooterHtml { get; set; } = "{{ server.name }} · <a href=\"{{ server.url }}\">{{ server.url }}</a>";

    /// <summary>Base URL used for links in mails; empty = the default share-link address.</summary>
    public string PublicBaseUrl { get; set; } = string.Empty;

    public void Normalize()
    {
        AccentColor = HexColor().IsMatch(AccentColor ?? string.Empty) ? AccentColor! : DefaultAccentColor;
        HeaderHtml ??= string.Empty;
        FooterHtml ??= string.Empty;
        PublicBaseUrl = (PublicBaseUrl ?? string.Empty).Trim().TrimEnd('/');
        if (PublicBaseUrl.Length > 0
            && !(Uri.TryCreate(PublicBaseUrl, UriKind.Absolute, out var uri)
                 && (uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp)))
            PublicBaseUrl = string.Empty;
        if (LogoDataUri is not null && !TryGetLogo(out _, out _))
            LogoDataUri = null;
    }

    /// <summary>Decodes <see cref="LogoDataUri"/> when it is a supported, size-capped image.</summary>
    public bool TryGetLogo(out byte[] bytes, out string contentType)
    {
        bytes = [];
        contentType = string.Empty;
        var match = LogoPattern().Match(LogoDataUri ?? string.Empty);
        if (!match.Success) return false;
        try { bytes = Convert.FromBase64String(match.Groups["data"].Value); }
        catch (FormatException) { return false; }
        contentType = "image/" + match.Groups["type"].Value;
        return bytes.Length is > 0 and <= MaxLogoBytes;
    }

    public static MailLayoutSettings Default() => new();

    [GeneratedRegex("^#[0-9A-Fa-f]{6}$")]
    private static partial Regex HexColor();

    [GeneratedRegex("^data:image/(?<type>png|jpeg|gif);base64,(?<data>[A-Za-z0-9+/=]+)$")]
    private static partial Regex LogoPattern();
}

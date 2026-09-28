using System.Collections.Concurrent;
using System.Net;
using System.Text.Encodings.Web;
using System.Text.RegularExpressions;
using Fluid;

namespace Kaimo_File_Server.Infrastructure.Notifications;

/// <summary>A rendered mail: plain-text subject, full HTML document and plain-text part.</summary>
public sealed record RenderedMail(string Subject, string Html, string Text);

/// <summary>
/// Renders notification mails with Fluid (Liquid). Sandboxed: templates only see a tree of
/// string dictionaries built from the placeholder values, never .NET objects, and loops are
/// bounded. Values are HTML-encoded in the body; the subject is plain text with CR/LF removed
/// so a value can never inject a mail header.
/// </summary>
public sealed partial class MailTemplateRenderer
{
    /// <summary>Content-ID of the inline logo attachment referenced by the layout.</summary>
    public const string LogoContentId = "kaimo-logo";

    private const int MaxCachedTemplates = 256;

    private static readonly FluidParser Parser = new();
    private static readonly TemplateOptions Options = new()
    {
        // Guards against runaway loops such as {% for i in (1..100000000) %}.
        MaxSteps = 100_000,
        MaxRecursion = 20,
    };

    // ponytail: unbounded growth is prevented by clearing the whole cache at the cap;
    // the live preview parses fresh sources on every edit anyway.
    private readonly ConcurrentDictionary<string, IFluidTemplate> _cache = new(StringComparer.Ordinal);

    /// <summary>Returns null when <paramref name="source"/> parses, otherwise the parser message (with line/column).</summary>
    public static string? Validate(string? source)
        => Parser.TryParse(source ?? string.Empty, out _, out var error) ? null : error;

    /// <summary>
    /// Renders <paramref name="template"/> for one recipient and wraps it into the layout.
    /// <paramref name="values"/> are flat placeholder values (<c>user.displayName</c> → …);
    /// <paramref name="logoSrc"/> is <c>cid:…</c> for a real mail or a data URI for the preview.
    /// </summary>
    public async Task<RenderedMail> RenderAsync(
        MailTemplateSource template,
        MailLayoutSettings layout,
        IReadOnlyDictionary<string, string> values,
        string language,
        string? logoSrc)
    {
        var context = CreateContext(values, layout, language, logoSrc);

        var subject = await Parse(template.Subject).RenderAsync(context, NullEncoder.Default);
        subject = LineBreaks().Replace(subject, " ").Trim();
        if (subject.Length > 900) subject = subject[..900];

        var content = await Parse(template.Html).RenderAsync(context, HtmlEncoder.Default);
        var header = await Parse(layout.HeaderHtml).RenderAsync(context, HtmlEncoder.Default);
        var footer = await Parse(layout.FooterHtml).RenderAsync(context, HtmlEncoder.Default);

        context.SetValue("subject", subject);
        context.SetValue("content", content);
        context.SetValue("header", header);
        context.SetValue("footer", footer);
        var html = await Parse(DefaultMailTemplates.Layout).RenderAsync(context, HtmlEncoder.Default);

        var text = string.IsNullOrWhiteSpace(template.Text)
            ? HtmlToText(content + "<hr>" + footer)
            : await Parse(template.Text).RenderAsync(context, NullEncoder.Default);

        return new RenderedMail(subject, html, text);
    }

    private IFluidTemplate Parse(string source)
    {
        if (_cache.TryGetValue(source, out var cached)) return cached;
        if (!Parser.TryParse(source, out var parsed, out var error))
            throw new InvalidOperationException("Invalid mail template: " + error);
        if (_cache.Count >= MaxCachedTemplates) _cache.Clear();
        return _cache[source] = parsed;
    }

    private static TemplateContext CreateContext(
        IReadOnlyDictionary<string, string> values, MailLayoutSettings layout, string language, string? logoSrc)
    {
        var context = new TemplateContext(Options);
        foreach (var (key, value) in Nest(values))
            context.SetValue(key, value);
        context.SetValue("layout", new Dictionary<string, object>
        {
            ["accent"] = layout.AccentColor,
            ["logo"] = logoSrc ?? string.Empty,
            ["language"] = language,
        });
        return context;
    }

    /// <summary>Turns <c>{"user.name": "x"}</c> into <c>{"user": {"name": "x"}}</c> so Liquid dot access works.</summary>
    internal static Dictionary<string, object> Nest(IReadOnlyDictionary<string, string> values)
    {
        var root = new Dictionary<string, object>(StringComparer.Ordinal);
        foreach (var (key, value) in values)
        {
            var parts = key.Split('.');
            var node = root;
            for (var i = 0; i < parts.Length - 1; i++)
            {
                if (node.GetValueOrDefault(parts[i]) is not Dictionary<string, object> child)
                    node[parts[i]] = child = new Dictionary<string, object>(StringComparer.Ordinal);
                node = child;
            }
            node[parts[^1]] = value;
        }
        return root;
    }

    /// <summary>A plain-text rendition for the text/plain part of the mail.</summary>
    internal static string HtmlToText(string html)
    {
        // Line-level tags break the line; block-level tags leave a blank line like a paragraph.
        var text = BlockBreaks().Replace(html, m =>
            m.Groups[1].Value.ToLowerInvariant() is "br" or "/li" ? "\n" : "\n\n");
        text = Links().Replace(text, m => m.Groups["text"].Value.Trim() == m.Groups["href"].Value
            ? m.Groups["href"].Value
            : $"{m.Groups["text"].Value} ({m.Groups["href"].Value})");
        text = Tags().Replace(text, string.Empty);
        text = WebUtility.HtmlDecode(text);
        text = Spaces().Replace(text, " ");
        text = BlankLines().Replace(text, "\n\n");
        return string.Join('\n', text.Split('\n').Select(l => l.Trim())).Trim();
    }

    [GeneratedRegex(@"[\r\n]+")]
    private static partial Regex LineBreaks();

    [GeneratedRegex(@"<\s*(br|/p|/div|/tr|/h[1-6]|/li|hr)[^>]*>", RegexOptions.IgnoreCase)]
    private static partial Regex BlockBreaks();

    [GeneratedRegex("<a\\s[^>]*href=\"(?<href>[^\"]*)\"[^>]*>(?<text>.*?)</a>", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex Links();

    [GeneratedRegex("<[^>]+>")]
    private static partial Regex Tags();

    [GeneratedRegex(@"[ \t]+")]
    private static partial Regex Spaces();

    [GeneratedRegex(@"\n\s*\n\s*(\n\s*)+")]
    private static partial Regex BlankLines();
}

using Kaimo_File_Server.Web.DynamicHelpers;
using Microsoft.AspNetCore.Components;
using System.Collections.Concurrent;
using System.Text;
using System.Text.RegularExpressions;

namespace Kaimo_File_Server.Web.Services;

public class AssetProvider
{
    
    private static readonly string SVG_HEADER =
        "<svg width=\"{width}\" height=\"{height}\" viewBox=\"0 0 24 24\" fill=\"{fill}\"\n {class} {style} stroke=\"{color}\" stroke-width=\"{stroke-width}\"\n     xmlns=\"http://www.w3.org/2000/svg\">";

    private static readonly string SVG_FOOTER = "</svg>";

    private static readonly HashSet<string> RELEVANT_SVG_TAGS = new()
    {
        "path",
        "line",
        "circle",
        "polyline",
        "polygon",
        "rect",
        "text",
        "g"
    };
    
    private readonly IWebHostEnvironment _env;
    // This provider is a singleton, so the cache is shared across all circuits and
    // threads — hence ConcurrentDictionary. The inline SVG icons are rendered per
    // visible row on every re-render (selection, sorting, etc.); re-reading and
    // re-parsing each file from disk on every one of those calls made every
    // interaction do dozens of file reads, which is especially slow over a bind mount.
    private readonly ConcurrentDictionary<string, string> _pathsCache = new();

    public AssetProvider(IWebHostEnvironment env)
    {
        _env = env;
    }

    /// <summary>
    /// If you need to load a raw svg, which just scales by width and height
    /// </summary>
    /// <param name="relativePath"></param>
    /// <param name="width"></param>
    /// <param name="height"></param>
    /// <returns></returns>
    public MarkupString RawSVG(string relativePath, int width, int height)
    {
        relativePath = "svg/" + relativePath;
        var fullPath = Path.Combine(_env.WebRootPath, relativePath);

        var svg = File.ReadAllText(fullPath);

        svg = Regex.Replace(
            svg,
            @"<svg\b([^>]*)>",
            match =>
            {
                var attributes = match.Groups[1].Value;

                // Vorhandene width/height entfernen
                attributes = Regex.Replace(
                    attributes,
                    @"\s+width\s*=\s*[""'][^""']*[""']",
                    ""
                );

                attributes = Regex.Replace(
                    attributes,
                    @"\s+height\s*=\s*[""'][^""']*[""']",
                    ""
                );

                // Neue Werte setzen
                return $"""<svg width="{width}" height="{height}"{attributes}>""";
            },
            RegexOptions.Singleline
        );

        return new MarkupString(svg);
    }

    public MarkupString SVG(string relativePath, SvgOptions options)
    {
        relativePath = "svg/" + relativePath;

        var svgPaths = loadSvgPaths(relativePath);
        var parameterizedSVG = parameterizeSVG(svgPaths, options);

        return new MarkupString(parameterizedSVG);
    }

    private string loadSvgPaths(string relativePath)
    {
        // Always cache, in every environment. Icons are static assets; re-reading and
        // re-parsing them from disk on every render (previously done in Development)
        // dominated the cost of routine interactions. Editing an SVG during development
        // now needs an app restart to take effect — an acceptable trade for the speed-up.
        return _pathsCache.GetOrAdd(relativePath, static (path, env) =>
        {
            var fullPath = Path.Combine(env.WebRootPath, path);

            // Normalize so every element sits on its own line regardless of how the
            // file was formatted (Inkscape spreads attributes across many lines),
            // then keep only the drawing elements.
            var flattened = Regex.Replace(File.ReadAllText(fullPath), @"\s+", " ").Replace("<", "\n<");
            return string.Join('\n', flattened.Split('\n')
                .Where(l => RELEVANT_SVG_TAGS.Any(tag => l.TrimStart().StartsWith("<" + tag))));
        }, _env);
    }

    private string parameterizeSVG(string svgPaths, SvgOptions options)
    {
        // set svg parameters
        var header = SVG_HEADER
                .Replace("{width}", options.Width.ToString())
                .Replace("{height}", options.Height.ToString())
                .Replace("{fill}", options.Fill)
                .Replace("{color}", options.Color)
                .Replace("{stroke-width}", options.StrokeWidth.ToString("0.0"))
                .Replace("{style}", options.Style is null ? "" : "style=\"" + options.Style +"\"")
                .Replace("{class}", options.CssClass is null ? "" : "class=\"" + options.CssClass +"\"")
            ;
        
        
        return header + svgPaths + SVG_FOOTER;
    }
}
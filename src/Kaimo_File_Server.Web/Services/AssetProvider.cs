using Kaimo_File_Server.Web.DynamicHelpers;
using Microsoft.AspNetCore.Components;
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
    private readonly Dictionary<string, string> _pathsCache = new();

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
        if (!_env.IsDevelopment() && _pathsCache.TryGetValue(relativePath, out var cached))
            return cached;
        
        var fullPath = Path.Combine(_env.WebRootPath, relativePath);
        
        // remove every tag that isn't part of the drawing
        var svgPaths = string.Join('\n', File.ReadAllLines(fullPath)
            .Where(l => RELEVANT_SVG_TAGS.Any(tag => l.TrimStart().StartsWith("<" + tag))));

        _pathsCache[relativePath] = svgPaths;
        return svgPaths;
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
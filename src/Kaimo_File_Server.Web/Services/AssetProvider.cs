using System.Text;
using Kaimo_File_Server.Web.DynamicHelpers;
using Microsoft.AspNetCore.Components;

namespace Kaimo_File_Server.Web.Services;

public class AssetProvider
{
    
    private static readonly string SVG_HEADER =
        "<svg width=\"{width}\" height=\"{height}\" viewBox=\"0 0 24 24\" fill=\"{fill}\"\n {class} {style} stroke=\"{color}\" stroke-width=\"{stroke-width}\"\n     xmlns=\"http://www.w3.org/2000/svg\">";

    private static readonly string SVG_FOOTER = "</svg>";

    private static readonly HashSet<string> RELEVANT_SVG_TAGS = new()
    {
        "<path",
        "<line"
    };
    
    private readonly IWebHostEnvironment _env;
    private readonly Dictionary<string, string> _pathsCache = new();

    public AssetProvider(IWebHostEnvironment env)
    {
        _env = env;
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
        if (_pathsCache.TryGetValue(relativePath, out var svgPaths) && !_env.IsDevelopment())
            return svgPaths;
        
        var fullPath = Path.Combine(_env.WebRootPath, relativePath);
        var rawSVG = File.ReadAllText(fullPath);
        
        // remove everything that is not a svg path
        var lines = rawSVG.Split("\n");
        svgPaths = string.Join('\n', lines.Where(l =>
        {
            string trimmedLine = l.Trim();
            
            foreach (var tag in RELEVANT_SVG_TAGS)
            {
                if (trimmedLine.StartsWith(tag))
                    return true;
            }

            return false;
        }));
        
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
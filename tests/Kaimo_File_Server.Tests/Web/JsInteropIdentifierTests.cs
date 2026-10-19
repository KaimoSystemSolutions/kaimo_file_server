using System.Text.RegularExpressions;
using Xunit;

namespace Kaimo_File_Server.Tests;

/// <summary>
/// Blazor resolves a JS interop identifier by walking its dot-separated segments as property
/// names on <c>window</c>. A call expression such as <c>document.getElementById('x').click</c>
/// therefore never resolves and throws on the circuit (an <c>async void</c> caller terminates
/// it). Every identifier passed to <c>InvokeAsync</c>/<c>InvokeVoidAsync</c> in the Web project
/// must be a plain dotted path to a function.
/// </summary>
public class JsInteropIdentifierTests
{
    private static readonly Regex InteropCall = new(
        @"Invoke(?:Void)?Async(?:<[^>]+>)?\(\s*""(?<id>[^""]*)""", RegexOptions.Compiled);

    private static readonly Regex PlainPath = new(
        @"^[A-Za-z_$][\w$]*(\.[A-Za-z_$][\w$]*)*$", RegexOptions.Compiled);

    [Fact]
    public void AllJsInteropIdentifiers_ArePlainDottedPaths()
    {
        string webRoot = FindWebProjectRoot();
        var offenders = Directory
            .EnumerateFiles(webRoot, "*.*", SearchOption.AllDirectories)
            .Where(f => f.EndsWith(".cs") || f.EndsWith(".razor"))
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
                        && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"))
            .SelectMany(f => InteropCall.Matches(File.ReadAllText(f))
                .Select(m => m.Groups["id"].Value)
                .Where(id => !PlainPath.IsMatch(id))
                .Select(id => $"{Path.GetFileName(f)}: {id}"))
            .ToList();

        Assert.Empty(offenders);
    }

    [Fact]
    public void FileUploadScript_ExportsTheFilePickerUsedByTheLayout()
    {
        string webRoot = FindWebProjectRoot();
        string layout = File.ReadAllText(Path.Combine(webRoot, "Components", "Layout", "MainLayout.razor"));
        string script = File.ReadAllText(Path.Combine(webRoot, "wwwroot", "js", "fileUpload.js"));

        Assert.Contains("\"kaimoUpload.openFilePicker\"", layout);
        Assert.Matches(@"return \{[^}]*\bopenFilePicker\b[^}]*\};", script);
    }

    private static string FindWebProjectRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            string candidate = Path.Combine(dir.FullName, "src", "Kaimo_File_Server.Web");
            if (Directory.Exists(candidate)) return candidate;
        }
        throw new DirectoryNotFoundException("src/Kaimo_File_Server.Web not found above the test output.");
    }
}

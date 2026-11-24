using System.Text.RegularExpressions;
using Xunit;

namespace Kaimo_File_Server.Web.Tests.Guards;

/// <summary>
/// Monospace text (paths, hashes, IDs, logs) uses one font stack, the <c>--font-mono</c> token
/// (DESIGN_SPEC §6). A literal stack per component drifts: three variants existed before.
/// </summary>
public partial class MonospaceFontTokenTests
{
    [Fact]
    public void FontMonoToken_IsDefined()
    {
        var tokens = File.ReadAllText(Path.Combine(WebRoot(), "wwwroot", "css", "app", "tokens.css"));

        Assert.Matches(@"--font-mono:\s*[^;]*monospace;", tokens);
    }

    [Fact]
    public void NoStylesheet_DeclaresItsOwnMonospaceStack()
    {
        var web = WebRoot();
        var offending = Directory.EnumerateFiles(web, "*.*", SearchOption.AllDirectories)
            .Where(f => f.EndsWith(".css", StringComparison.Ordinal) || f.EndsWith(".cs", StringComparison.Ordinal))
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}")
                        && !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}"))
            .SelectMany(f => LiteralMonospace().Matches(File.ReadAllText(f))
                .Select(m => Path.GetRelativePath(web, f) + ": " + m.Value))
            .ToList();

        Assert.Empty(offending);
    }

    // "font-family: …monospace" or the "font: … monospace" shorthand, but not var(--font-mono).
    [GeneratedRegex(@"\bfont(-family)?\s*:[^;}]*\bmonospace")]
    private static partial Regex LiteralMonospace();

    private static string WebRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Kaimo_File_Server.slnx")))
            dir = dir.Parent;
        return Path.Combine(dir?.FullName ?? throw new DirectoryNotFoundException("Repository root not found."),
            "src", "Kaimo_File_Server.Web");
    }
}

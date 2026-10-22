using System.Globalization;
using System.Text.RegularExpressions;
using Xunit;

namespace Kaimo_File_Server.Tests;

/// <summary>
/// DESIGN_SPEC §337: text must reach WCAG AA (4.5:1) per theme and per accent. Guards the
/// token pairs that carry small text across the whole UI: label on primary buttons, muted
/// helper text and the "positive" status text.
/// </summary>
public class ThemeTokenContrastTests
{
    private const double Aa = 4.5;
    private static readonly string[] Accents = ["green", "magenta", "orange", "red", "blue", "yellow", "turquoise"];

    public static TheoryData<string, string> ThemeAccents()
    {
        var data = new TheoryData<string, string>();
        foreach (var theme in new[] { "dark", "light" })
            foreach (var accent in Accents)
                data.Add(theme, accent);
        return data;
    }

    [Theory]
    [MemberData(nameof(ThemeAccents))]
    public void PrimaryButtonLabel_MeetsAa(string theme, string accent)
    {
        var t = Tokens(theme, accent);
        AssertContrast(t, "--text-on-accent", "--accent", theme, accent);
    }

    [Theory]
    [InlineData("light", "--bg-primary")]
    [InlineData("light", "--bg-secondary")]
    [InlineData("light", "--bg-tertiary")]
    [InlineData("light", "--bg-sunken")]
    [InlineData("light", "--accent-subtle")]
    [InlineData("light", "--accent-selected")]
    [InlineData("dark", "--bg-primary")]
    [InlineData("dark", "--bg-secondary")]
    [InlineData("dark", "--bg-tertiary")]
    public void MutedText_MeetsAa(string theme, string background)
        => AssertContrast(Tokens(theme, "green"), "--text-tertiary", background, theme, "green");

    [Theory]
    [InlineData("light", "--bg-secondary")]
    [InlineData("light", "--bg-tertiary")]
    [InlineData("dark", "--bg-secondary")]
    [InlineData("dark", "--bg-tertiary")]
    public void SuccessText_MeetsAa(string theme, string background)
        => AssertContrast(Tokens(theme, "green"), "--success", background, theme, "green");

    private static void AssertContrast(IReadOnlyDictionary<string, string> t, string fg, string bg, string theme, string accent)
    {
        var ratio = Contrast(Resolve(t, fg), Resolve(t, bg));
        Assert.True(ratio >= Aa, $"{theme}/{accent}: {fg} on {bg} = {ratio:F2}:1 (< {Aa})");
    }

    // -- tokens.css parsing --

    private static readonly Regex Block = new(@"(?<sel>\[data-theme=""[a-z]+""\](\[data-accent=""[a-z]+""\])?)\s*\{(?<body>[^}]*)\}", RegexOptions.Compiled);
    private static readonly Regex Decl = new(@"(?<name>--[\w-]+)\s*:\s*(?<value>[^;]+);", RegexOptions.Compiled);

    private static Dictionary<string, string> Tokens(string theme, string accent)
    {
        var css = File.ReadAllText(Path.Combine(MailNotificationTests.FindWebRoot(), "css", "app", "tokens.css"));
        var baseSel = $"[data-theme=\"{theme}\"]";
        var accentSel = $"{baseSel}[data-accent=\"{accent}\"]";
        var tokens = new Dictionary<string, string>();
        foreach (var sel in new[] { baseSel, accentSel })
            foreach (Match b in Block.Matches(css))
                if (b.Groups["sel"].Value == sel)
                    foreach (Match d in Decl.Matches(b.Groups["body"].Value))
                        tokens[d.Groups["name"].Value] = d.Groups["value"].Value.Trim();
        return tokens;
    }

    private static string Resolve(IReadOnlyDictionary<string, string> t, string name)
    {
        var value = t[name];
        var m = Regex.Match(value, @"^var\((--[\w-]+)\)$");
        return m.Success ? Resolve(t, m.Groups[1].Value) : value;
    }

    private static double Contrast(string a, string b)
    {
        double la = Luminance(a), lb = Luminance(b);
        return (Math.Max(la, lb) + 0.05) / (Math.Min(la, lb) + 0.05);
    }

    private static double Luminance(string hex)
    {
        hex = hex.TrimStart('#');
        double Channel(int i)
        {
            var v = int.Parse(hex.Substring(i, 2), NumberStyles.HexNumber) / 255.0;
            return v <= 0.03928 ? v / 12.92 : Math.Pow((v + 0.055) / 1.055, 2.4);
        }
        return 0.2126 * Channel(0) + 0.7152 * Channel(2) + 0.0722 * Channel(4);
    }
}

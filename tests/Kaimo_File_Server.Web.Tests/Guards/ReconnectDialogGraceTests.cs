using System.Globalization;
using System.Text.RegularExpressions;
using Xunit;
using Kaimo_File_Server.Web.Tests.Services;

namespace Kaimo_File_Server.Web.Tests.Guards;

/// <summary>
/// The reconnect dialog (App.razor, styled in base.css) must not flash for short connection drops
/// such as switching apps on a phone: while Blazor is still reconnecting, the overlay only becomes
/// visible after a grace delay. States the user has to act on (failed, rejected, paused) appear at once.
/// </summary>
public class ReconnectDialogGraceTests
{
    private const string Modal = "#components-reconnect-modal";
    private static readonly Regex Rule = new(@"(?<sel>[^{}]+)\{(?<body>[^{}]*)\}", RegexOptions.Compiled);

    [Theory]
    [InlineData("components-reconnect-show")]
    [InlineData("components-reconnect-retrying")]
    public void ReconnectingState_RevealsOnlyAfterGraceDelay(string state)
    {
        var declarations = DeclarationsFor(state);

        var delay = EffectiveDelay(declarations);
        Assert.True(delay is >= 1 and <= 5, $"{state}: grace delay {delay}s should be between 1s and 5s");

        var animation = Value(declarations, "animation");
        Assert.Matches(@"\b(both|backwards)\b", animation);

        var keyframesName = animation.Split(' ', StringSplitOptions.RemoveEmptyEntries)[0];
        var keyframes = Regex.Match(Css(), @"@keyframes\s+" + Regex.Escape(keyframesName) + @"\s*\{\s*from\s*\{(?<from>[^}]*)\}");
        Assert.True(keyframes.Success, $"@keyframes {keyframesName} with a 'from' step not found");
        Assert.Matches(@"opacity\s*:\s*0\s*;?", keyframes.Groups["from"].Value);
    }

    [Theory]
    [InlineData("components-reconnect-failed")]
    [InlineData("components-reconnect-resume-failed")]
    [InlineData("components-reconnect-rejected")]
    [InlineData("components-reconnect-paused")]
    public void ActionableState_ShowsImmediately(string state)
    {
        var declarations = DeclarationsFor(state);

        Assert.DoesNotContain(declarations, d => d.Name is "animation" or "animation-delay");
        Assert.Contains(declarations, d => d.Name == "display" && d.Value == "flex");
    }

    /// <summary>Declarations of every rule whose selector list targets the modal itself in <paramref name="state"/>.</summary>
    private static List<(string Name, string Value)> DeclarationsFor(string state)
    {
        var target = $"{Modal}.{state}";
        var declarations = new List<(string, string)>();
        foreach (Match m in Rule.Matches(StripComments(Css())))
        {
            var selectors = m.Groups["sel"].Value.Split(',').Select(s => s.Trim());
            if (!selectors.Contains(target)) continue;
            foreach (var d in m.Groups["body"].Value.Split(';'))
            {
                var parts = d.Split(':', 2);
                if (parts.Length == 2) declarations.Add((parts[0].Trim(), parts[1].Trim()));
            }
        }
        Assert.NotEmpty(declarations);
        return declarations;
    }

    private static string Value(List<(string Name, string Value)> declarations, string name)
    {
        var match = declarations.LastOrDefault(d => d.Name == name);
        Assert.False(match.Name is null, $"'{name}' not declared");
        return match.Value;
    }

    /// <summary>
    /// The animation delay that actually applies, honouring source order: the <c>animation</c> shorthand
    /// resets the delay (to its second time value, or 0s) and thereby overrides any earlier <c>animation-delay</c>.
    /// </summary>
    private static double EffectiveDelay(List<(string Name, string Value)> declarations)
    {
        double delay = 0;
        foreach (var (name, value) in declarations)
        {
            if (name == "animation-delay")
            {
                delay = Seconds(value);
            }
            else if (name == "animation")
            {
                var times = value.Split(' ', StringSplitOptions.RemoveEmptyEntries).Where(IsTime).ToList();
                delay = times.Count > 1 ? Seconds(times[1]) : 0;
            }
        }
        return delay;
    }

    private static bool IsTime(string token) => Regex.IsMatch(token, @"^-?\d*\.?\d+m?s$");

    private static double Seconds(string value) => value.EndsWith("ms", StringComparison.Ordinal)
        ? double.Parse(value[..^2], CultureInfo.InvariantCulture) / 1000
        : double.Parse(value.TrimEnd('s'), CultureInfo.InvariantCulture);

    private static string StripComments(string css) => Regex.Replace(css, @"/\*.*?\*/", "", RegexOptions.Singleline);

    private static string Css()
        => File.ReadAllText(Path.Combine(MailNotificationTests.FindWebRoot(), "css", "app", "base.css"));
}

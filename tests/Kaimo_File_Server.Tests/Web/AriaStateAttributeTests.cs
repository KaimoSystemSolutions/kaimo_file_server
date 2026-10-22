using System.Text.RegularExpressions;
using Bunit;
using Kaimo_File_Server.Web.Components.Shared;
using Kaimo_File_Server.Web.Services;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using Xunit;

namespace Kaimo_File_Server.Tests;

/// <summary>
/// Blazor renders a bool-bound attribute as <c>attr=""</c> when true and drops it when false.
/// For ARIA state attributes that is invalid: screen readers then see no selected tab, no
/// pressed toggle and no expanded menu. Their values must be the strings "true"/"false".
/// </summary>
public class AriaStateAttributeTests
{
    [Theory]
    [InlineData(true, "true")]
    [InlineData(false, "false")]
    public void Tab_RendersAriaSelectedAsString(bool active, string expected)
    {
        using var ctx = new BunitContext();
        var env = new Mock<Microsoft.AspNetCore.Hosting.IWebHostEnvironment>();
        env.SetupGet(e => e.WebRootPath).Returns(MailNotificationTests.FindWebRoot());
        ctx.Services.AddSingleton(new AssetProvider(env.Object));

        var cut = ctx.Render<Tab>(p => p.Add(t => t.Active, active).Add(t => t.Label, "Tab"));

        Assert.Equal(expected, cut.Find("button[role=tab]").GetAttribute("aria-selected"));
    }

    // aria-*="@expr" whose expression does not itself produce the "true"/"false" strings.
    private static readonly Regex BoolBoundAriaState = new(
        "aria-(selected|pressed|expanded|checked)=\"@(?<expr>\\(.*?\\)|[\\w.!]+)\"",
        RegexOptions.Compiled);

    [Fact]
    public void RazorMarkup_BindsAriaStateAttributesToStrings()
    {
        var projectDir = Directory.GetParent(MailNotificationTests.FindWebRoot())!.FullName;
        var offenders = new List<string>();

        foreach (var file in Directory.EnumerateFiles(Path.Combine(projectDir, "Components"), "*.razor", SearchOption.AllDirectories))
        {
            var lines = File.ReadAllLines(file);
            for (var i = 0; i < lines.Length; i++)
            {
                foreach (System.Text.RegularExpressions.Match m in BoolBoundAriaState.Matches(lines[i]))
                {
                    if (!m.Groups["expr"].Value.Contains("\"true\"", StringComparison.Ordinal))
                        offenders.Add($"{Path.GetRelativePath(projectDir, file)}:{i + 1}: {m.Value}");
                }
            }
        }

        Assert.True(offenders.Count == 0, "ARIA state bound to a bool:\n" + string.Join("\n", offenders));
    }
}

using Bunit;
using Kaimo_File_Server.Core.Domain.Identity;
using Kaimo_File_Server.Core.Language;
using Kaimo_File_Server.Core.Security;
using Kaimo_File_Server.Web.Components.Pages.Security.Components;
using Xunit;

namespace Kaimo_File_Server.Tests;

/// <summary>
/// Result cell of the security overview's attempt list: the outcome badge, plus a "×N" badge
/// when repeated LockedOut attempts of one lockout were folded into the row.
/// </summary>
public class LoginAttemptResultTests
{
    private static IRenderedComponent<LoginAttemptResult> Render(BunitContext ctx, LoginOutcome outcome, int repeats)
        => ctx.Render<LoginAttemptResult>(p => p.Add(c => c.Attempt, new LoginAttemptRecord
        {
            Outcome = outcome,
            RepeatCount = repeats,
        }));

    [Fact]
    public void RepeatedLockout_ShowsTheRepeatBadge()
    {
        using var ctx = new BunitContext();

        var cut = Render(ctx, LoginOutcome.LockedOut, 10);

        var badges = cut.FindAll(".status-badge");
        Assert.Equal(2, badges.Count);
        Assert.Contains("status-badge--warning", badges[0].ClassName);
        var repeat = cut.Find(".login-attempt-repeat");
        Assert.Equal("×10", repeat.TextContent.Trim());
        Assert.Contains("10", repeat.GetAttribute("title"));
        Assert.Equal(repeat.GetAttribute("title"), repeat.GetAttribute("aria-label"));
    }

    // SMB successes are one counted row per account, address and hour (AP8d).
    [Fact]
    public void CountedSuccesses_ShowTheCountAsSuccessfulLogins()
    {
        using var ctx = new BunitContext();

        var cut = Render(ctx, LoginOutcome.Success, 4);

        var repeat = cut.Find(".login-attempt-repeat");
        Assert.Equal("×4", repeat.TextContent.Trim());
        var hint = Resources.ResourceManager.GetString("Web_Security_RepeatCount_SuccessHint");
        Assert.False(string.IsNullOrEmpty(hint));
        Assert.Equal(string.Format(hint!, 4), repeat.GetAttribute("title"));
    }

    // SMB failures are folded the same way, per account (or address), address and hour.
    [Theory]
    [InlineData(LoginOutcome.InvalidCredentials)]
    [InlineData(LoginOutcome.AccountDisabled)]
    public void CountedFailures_ShowTheCountAsFailedLogins(LoginOutcome outcome)
    {
        using var ctx = new BunitContext();

        var cut = Render(ctx, outcome, 5);

        var repeat = cut.Find(".login-attempt-repeat");
        Assert.Equal("×5", repeat.TextContent.Trim());
        var hint = Resources.ResourceManager.GetString("Web_Security_RepeatCount_FailureHint");
        Assert.False(string.IsNullOrEmpty(hint));
        Assert.Equal(string.Format(hint!, 5), repeat.GetAttribute("title"));
    }

    [Theory]
    [InlineData(LoginOutcome.LockedOut)]
    [InlineData(LoginOutcome.InvalidCredentials)]
    [InlineData(LoginOutcome.Success)]
    public void SingleAttempt_ShowsOnlyTheOutcome(LoginOutcome outcome)
    {
        using var ctx = new BunitContext();

        var cut = Render(ctx, outcome, 1);

        Assert.Single(cut.FindAll(".status-badge"));
        Assert.Empty(cut.FindAll(".login-attempt-repeat"));
        Assert.DoesNotContain("×", cut.Markup);
    }

    [Fact]
    public void Outcomes_KeepTheirTones()
    {
        using var ctx = new BunitContext();

        Assert.Contains("status-badge--positive", Render(ctx, LoginOutcome.Success, 1).Find(".status-badge").ClassName);
        Assert.Contains("status-badge--negative", Render(ctx, LoginOutcome.InvalidCredentials, 1).Find(".status-badge").ClassName);
        Assert.Contains("status-badge--neutral", Render(ctx, LoginOutcome.AccountDisabled, 1).Find(".status-badge").ClassName);
    }

    [Fact]
    public void AddressLockout_HasItsOwnLabel_AndTheRepeatBadge()
    {
        using var ctx = new BunitContext();

        var cut = Render(ctx, LoginOutcome.AddressLockedOut, 7);

        var outcome = cut.FindAll(".status-badge")[0];
        Assert.Contains("status-badge--warning", outcome.ClassName);
        var label = Resources.ResourceManager.GetString("Web_Security_Outcome_AddressLocked");
        Assert.False(string.IsNullOrEmpty(label));
        Assert.Equal(label, outcome.TextContent.Trim());
        Assert.NotEqual(Resources.ResourceManager.GetString("Web_Security_Outcome_Locked"), label);
        Assert.Equal("×7", cut.Find(".login-attempt-repeat").TextContent.Trim());
    }
}

using Bunit;
using Kaimo_File_Server.Web.Components.Shared;
using Kaimo_File_Server.Web.Services;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using Xunit;

namespace Kaimo_File_Server.Tests;

/// <summary>
/// The info button is placed inside &lt;label&gt; elements all over the admin UI. It must
/// therefore never be a labelable element itself: a label forwards hover and clicks to its
/// first labelable descendant, which made the tooltip open when hovering or clicking anywhere
/// in the label (and stole the field's label association). Rich content shares the tooltip
/// look instead of a separate dismissible popover.
/// </summary>
public class InfoButtonTests
{
    private static BunitContext CreateContext()
    {
        var ctx = new BunitContext();
        var env = new Mock<Microsoft.AspNetCore.Hosting.IWebHostEnvironment>();
        env.SetupGet(e => e.WebRootPath).Returns(MailNotificationTests.FindWebRoot());
        ctx.Services.AddSingleton(new AssetProvider(env.Object));
        return ctx;
    }

    private static readonly string[] LabelableTags = ["button", "input", "select", "textarea", "meter", "output", "progress"];

    [Fact]
    public void Trigger_IsNotALabelableElement()
    {
        using var ctx = CreateContext();
        var cut = ctx.Render<InfoButton>(p => p.Add(b => b.Text, "Hint"));

        foreach (var tag in LabelableTags)
            Assert.Empty(cut.FindAll(tag));

        var trigger = cut.Find(".info-btn__trigger");
        Assert.Equal("button", trigger.GetAttribute("role"));
        Assert.Equal("0", trigger.GetAttribute("tabindex"));
    }

    [Fact]
    public void Text_RendersTooltipDescribingTrigger()
    {
        using var ctx = CreateContext();
        var cut = ctx.Render<InfoButton>(p => p.Add(b => b.Text, "Hint"));

        var tip = cut.Find("[role=tooltip]");
        Assert.Equal("Hint", tip.TextContent.Trim());
        Assert.Equal(tip.Id, cut.Find(".info-btn__trigger").GetAttribute("aria-describedby"));
    }

    [Fact]
    public void RichContent_RendersAsTooltipWithTitle_NoDialogOrCloseButton()
    {
        using var ctx = CreateContext();
        RenderFragment body = b => { b.OpenElement(0, "p"); b.AddContent(1, "Body text"); b.CloseElement(); };
        var cut = ctx.Render<InfoButton>(p => p.Add(b => b.Title, "Heading").Add(b => b.ChildContent, body));

        var tip = cut.Find("[role=tooltip]");
        Assert.Contains("Heading", tip.TextContent);
        Assert.Contains("Body text", tip.TextContent);
        Assert.Empty(cut.FindAll("[role=dialog]"));
        Assert.Empty(cut.FindAll(".info-btn__close"));
        Assert.Empty(cut.FindAll(".info-btn__backdrop"));
    }

    [Fact]
    public void Click_PinsTooltip_AndSecondClickReleasesIt()
    {
        using var ctx = CreateContext();
        var cut = ctx.Render<InfoButton>(p => p.Add(b => b.Text, "Hint"));

        cut.Find(".info-btn__trigger").Click();
        Assert.Contains("info-btn--open", cut.Find(".info-btn").ClassName);

        cut.Find(".info-btn__trigger").Click();
        Assert.DoesNotContain("info-btn--open", cut.Find(".info-btn").ClassName);
    }

    [Theory]
    [InlineData("Enter")]
    [InlineData(" ")]
    public void EnterOrSpace_TogglesLikeANativeButton(string key)
    {
        using var ctx = CreateContext();
        var cut = ctx.Render<InfoButton>(p => p.Add(b => b.Text, "Hint"));

        cut.Find(".info-btn__trigger").KeyDown(new KeyboardEventArgs { Key = key });

        Assert.Contains("info-btn--open", cut.Find(".info-btn").ClassName);
    }

    [Fact]
    public void Escape_MouseLeaveAndFocusOut_ReleasePinnedTooltip()
    {
        using var ctx = CreateContext();
        var cut = ctx.Render<InfoButton>(p => p.Add(b => b.Text, "Hint"));
        var open = () => Assert.Contains("info-btn--open", cut.Find(".info-btn").ClassName);
        var closed = () => Assert.DoesNotContain("info-btn--open", cut.Find(".info-btn").ClassName);

        cut.Find(".info-btn__trigger").Click(); open();
        cut.Find(".info-btn__trigger").KeyDown(new KeyboardEventArgs { Key = "Escape" }); closed();

        cut.Find(".info-btn__trigger").Click(); open();
        cut.Find(".info-btn__trigger").MouseLeave(); closed();

        cut.Find(".info-btn__trigger").Click(); open();
        cut.Find(".info-btn__trigger").FocusOut(); closed();
    }
}

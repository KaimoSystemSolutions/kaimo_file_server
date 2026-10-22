using System.Globalization;
using Bunit;
using Kaimo_File_Server.Core.Security;
using Kaimo_File_Server.Web.Components.Pages.Auth;
using Kaimo_File_Server.Web.Components.ViewModels;
using Kaimo_File_Server.Web.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.JSInterop;
using Moq;
using Xunit;

namespace Kaimo_File_Server.Tests;

/// <summary>
/// The login page follows the UI language (no hard-coded strings) and every field
/// has a programmatically associated label.
/// </summary>
public class LoginPageTests
{
    [Theory]
    [InlineData("en", "Sign in", "Username", "Password")]
    [InlineData("de", "Anmelden", "Benutzername", "Passwort")]
    public void Login_UsesUiLanguage(string culture, string submit, string user, string password)
    {
        var previous = CultureInfo.CurrentUICulture;
        CultureInfo.CurrentUICulture = new CultureInfo(culture);
        try
        {
            using var ctx = NewContext();
            var cut = ctx.Render<Login>();

            Assert.Equal(submit, cut.Find(".login-form button").TextContent.Trim());
            var labels = cut.FindAll(".login-form label").Select(l => l.TextContent.Trim()).ToList();
            Assert.Equal([user, password], labels);
        }
        finally
        {
            CultureInfo.CurrentUICulture = previous;
        }
    }

    [Fact]
    public void Login_LabelsPointAtTheirInputs()
    {
        using var ctx = NewContext();
        var cut = ctx.Render<Login>();

        foreach (var label in cut.FindAll(".login-form label"))
        {
            var target = label.GetAttribute("for");
            Assert.False(string.IsNullOrEmpty(target), $"label '{label.TextContent}' has no for");
            Assert.Single(cut.FindAll($"input#{target}"));
        }
    }

    [Fact]
    public void Login_SubmitUsesTheCanonicalButtonClasses()
    {
        using var ctx = NewContext();
        var cut = ctx.Render<Login>();

        var classes = cut.Find(".login-form button").ClassList;
        Assert.Contains("btn", classes);
        Assert.Contains("btn-primary", classes);
    }

    private static BunitContext NewContext()
    {
        var ctx = new BunitContext();
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        var env = new Mock<Microsoft.AspNetCore.Hosting.IWebHostEnvironment>();
        env.SetupGet(e => e.WebRootPath).Returns(MailNotificationTests.FindWebRoot());
        ctx.Services.AddSingleton(new AssetProvider(env.Object));

        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Jwt:Secret"] = "unit-test-jwt-secret-at-least-32-characters-long!!",
                ["Jwt:Issuer"] = "KaimoFileServer",
            })
            .Build();
        var jwt = new JwtTokenService(config, NullLogger<JwtTokenService>.Instance);
        var scopeFactory = new ServiceCollection().BuildServiceProvider().GetRequiredService<IServiceScopeFactory>();
        var authState = new JwtAuthenticationStateProvider(
            Mock.Of<IJSRuntime>(), jwt, new WebSessionTicketStore(), scopeFactory,
            NullLogger<JwtAuthenticationStateProvider>.Instance);
        ctx.Services.AddSingleton(new LoginViewModel(
            Mock.Of<ILoginService>(), jwt, authState, NullLogger<LoginViewModel>.Instance, new ClientConnectionInfo()));
        return ctx;
    }
}

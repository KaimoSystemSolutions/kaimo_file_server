using Kaimo_File_Server.Core.Domain;
using Kaimo_File_Server.Infrastructure.Notifications;
using Kaimo_File_Server.Web.Services;
using Xunit;

namespace Kaimo_File_Server.Tests;

/// <summary>
/// Pure-policy tests for <see cref="ShareLink"/> window/exhaustion gating and the
/// <see cref="ServerDomainSettings"/> address handling used to build link URLs.
/// </summary>
public class ShareLinkPolicyTests
{
    private static readonly DateTime Now = new(2026, 6, 1, 12, 0, 0, DateTimeKind.Utc);

    private static ShareLink Link(
        bool enabled = true, DateTime? start = null, DateTime? expiry = null,
        int? maxAccess = null, int accessCount = 0)
        => new()
        {
            IsEnabled = enabled,
            StartsAtUtc = start,
            ExpiresAtUtc = expiry,
            MaxAccessCount = maxAccess,
            AccessCount = accessCount,
        };

    [Fact]
    public void WindowOpen_TrueWhenEnabledAndNoBounds()
        => Assert.True(Link().IsWindowOpen(Now));

    [Fact]
    public void WindowOpen_FalseWhenDisabled()
        => Assert.False(Link(enabled: false).IsWindowOpen(Now));

    [Fact]
    public void WindowOpen_FalseBeforeStart()
        => Assert.False(Link(start: Now.AddHours(1)).IsWindowOpen(Now));

    [Fact]
    public void WindowOpen_FalseAfterExpiry()
        => Assert.False(Link(expiry: Now.AddHours(-1)).IsWindowOpen(Now));

    [Fact]
    public void WindowOpen_TrueInsideWindow()
        => Assert.True(Link(start: Now.AddHours(-1), expiry: Now.AddHours(1)).IsWindowOpen(Now));

    [Fact]
    public void CurrentlyActive_FalseWhenExhausted()
        => Assert.False(Link(maxAccess: 3, accessCount: 3).IsCurrentlyActive(Now));

    [Fact]
    public void CurrentlyActive_TrueBelowMax()
        => Assert.True(Link(maxAccess: 3, accessCount: 2).IsCurrentlyActive(Now));

    [Fact]
    public void CurrentlyActive_TrueWhenUnlimited()
        => Assert.True(Link(maxAccess: null, accessCount: 9999).IsCurrentlyActive(Now));

    [Fact]
    public void Domains_Normalize_DropsInvalidNonHttpAndDuplicates()
    {
        var domains = new ServerDomainSettings
        {
            Addresses = new()
            {
                "https://a.example.com/", "not a url", "https://a.example.com", // duplicate (trailing slash)
                "javascript:alert(1)", "ftp://b.example.com", "http://c.example.com:8080",
            },
            DefaultIndex = 20,
        };

        domains.Normalize();

        Assert.Equal(new[] { "https://a.example.com", "http://c.example.com:8080" }, domains.Addresses);
        Assert.Equal(1, domains.DefaultIndex); // clamped
        Assert.Equal("c.example.com", ServerDomainSettings.HostOf(domains.Addresses[1]));
    }

    [Fact]
    public void Domains_FromLegacy_MergesShareLinkAddressesAndMailUrl()
    {
        var legacy = ServerDomainSettings.FromLegacy(
            new ShareLinkSettings { BaseAddresses = new() { "https://a.example.com", "https://b.example.com" }, DefaultIndex = 1 },
            new MailLayoutSettings { PublicBaseUrl = "https://mail.example.com/" });

        Assert.Equal(new[] { "https://a.example.com", "https://b.example.com", "https://mail.example.com" }, legacy.Addresses);
        Assert.Equal("https://b.example.com", legacy.DefaultAddress); // share-link default wins

        // Without share-link addresses the mail URL becomes the default.
        var mailOnly = ServerDomainSettings.FromLegacy(
            ShareLinkSettings.Default(), new MailLayoutSettings { PublicBaseUrl = "https://mail.example.com" });
        Assert.Equal("https://mail.example.com", mailOnly.DefaultAddress);
    }

    [Fact]
    public void BuildUrl_UsesDefaultDomain_ThenBrowserAddress_ThenRelative()
    {
        var domains = new ServerDomainSettings { Addresses = new() { "https://files.example.com" } };

        Assert.Equal("https://files.example.com/shared/abc",
            ShareLinkService.BuildUrl("abc", domains, "https://browser.example.com/"));
        Assert.Equal("https://browser.example.com/shared/abc",
            ShareLinkService.BuildUrl("abc", ServerDomainSettings.Default(), "https://browser.example.com/"));
        Assert.Equal("/shared/abc", ShareLinkService.BuildUrl("abc", ServerDomainSettings.Default(), null));
    }
}

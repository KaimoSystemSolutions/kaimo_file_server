using Kaimo_File_Server.Core.Domain;
using Kaimo_File_Server.Web.Services;
using Xunit;

namespace Kaimo_File_Server.Tests;

/// <summary>
/// Pure-policy tests for <see cref="ShareLink"/> window/exhaustion gating and the
/// <see cref="ShareLinkSettings"/> base-address allowlist.
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
    public void Settings_Normalize_DropsInvalidAndCapsAtFive()
    {
        var settings = new ShareLinkSettings
        {
            BaseAddresses = new()
            {
                "https://a.example.com/", "not a url", "https://a.example.com", // duplicate (trailing slash)
                "https://b.example.com", "https://c.example.com", "https://d.example.com",
                "https://e.example.com", "https://f.example.com", // 6th valid → dropped by cap
            },
            DefaultIndex = 20,
        };

        settings.Normalize();

        Assert.Equal(5, settings.BaseAddresses.Count);
        Assert.DoesNotContain("not a url", settings.BaseAddresses);
        Assert.Equal("https://a.example.com", settings.BaseAddresses[0]); // trailing slash trimmed, deduped
        Assert.InRange(settings.DefaultIndex, 0, 4);
    }

    [Fact]
    public void Settings_ResolveBaseFor_HonoursAllowlistAndToggle()
    {
        var settings = new ShareLinkSettings
        {
            BaseAddresses = new() { "https://default.example.com", "https://alt.example.com" },
            DefaultIndex = 0,
            AllowUserChosenAddress = true,
        };
        settings.Normalize();

        // Allowed + valid choice → used.
        Assert.Equal("https://alt.example.com", settings.ResolveBaseFor("https://alt.example.com"));
        // Choice not in the allowlist → default.
        Assert.Equal("https://default.example.com", settings.ResolveBaseFor("https://evil.example.com"));

        // Toggle off → always default, even for an allowlisted choice.
        settings.AllowUserChosenAddress = false;
        Assert.Equal("https://default.example.com", settings.ResolveBaseFor("https://alt.example.com"));
    }
}

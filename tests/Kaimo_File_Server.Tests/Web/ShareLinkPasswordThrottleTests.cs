using Kaimo_File_Server.Core.Domain;
using Kaimo_File_Server.Core.Repositories;
using Kaimo_File_Server.Core.Security;
using Kaimo_File_Server.Core.Services;
using Kaimo_File_Server.Infrastructure.Configuration;
using Kaimo_File_Server.Web.Services;
using Microsoft.AspNetCore.Components.Authorization;
using Moq;
using Xunit;

namespace Kaimo_File_Server.Tests;

/// <summary>
/// Share-link passwords are throttled per (link, client address) and per link, with the
/// thresholds of <see cref="LoginProtectionSettings"/>: one address guessing only locks itself
/// out, while guessing spread over many addresses still locks the link for everyone.
/// </summary>
public sealed class ShareLinkPasswordThrottleTests
{
    private const string AddressA = "203.0.113.10";
    private const string AddressB = "198.51.100.20";

    private readonly Mock<IPasswordService> _passwords = new();
    private readonly Mock<IConfigRepository> _config = new();
    private readonly LoginThrottle _throttle = new();
    private readonly ShareLink _link = new()
    {
        Id = Guid.NewGuid(), Token = "abc123", ShareId = Guid.NewGuid(), RootRelativePath = "docs",
        IsDirectory = true, CreatedByUserId = Guid.NewGuid(), PasswordHash = "link-hash"
    };

    public ShareLinkPasswordThrottleTests()
    {
        _config.Setup(c => c.GetIntAsync(It.IsAny<string>(), It.IsAny<int>()))
            .ReturnsAsync((string _, int fallback) => fallback);
        _config.Setup(c => c.GetBoolAsync(It.IsAny<string>(), It.IsAny<bool>()))
            .ReturnsAsync((string _, bool fallback) => fallback);
        _passwords.Setup(p => p.VerifyPassword("right", "link-hash")).Returns(true);
    }

    [Fact]
    public async Task AddressBudget_LocksOnlyThatAddress_OtherAddressStillUnlocks()
    {
        var service = Service();
        for (int i = 0; i < LoginProtectionSettings.DefaultShareLinkMaxAttemptsPerAddress; i++)
            Assert.False((await service.VerifyPasswordAsync(_link, "wrong", AddressA)).Ok);

        var lockedA = await service.VerifyPasswordAsync(_link, "right", AddressA);
        Assert.False(lockedA.Ok);
        Assert.True(lockedA.RetryAfter > TimeSpan.Zero);

        Assert.False((await service.VerifyPasswordAsync(_link, "wrong", AddressB)).Ok);
        Assert.True((await service.VerifyPasswordAsync(_link, "right", AddressB)).Ok);
    }

    [Fact]
    public async Task LockedAddress_KeepsGuessing_DoesNotLockTheLink()
    {
        var service = Service();
        for (int i = 0; i < LoginProtectionSettings.DefaultShareLinkMaxAttemptsPerAddress; i++)
            await service.VerifyPasswordAsync(_link, "wrong", AddressA);

        // Far more than the link budget, all refused by the address lock before they count.
        for (int i = 0; i < 2 * LoginProtectionSettings.DefaultShareLinkMaxAttemptsPerLink; i++)
            Assert.False((await service.VerifyPasswordAsync(_link, "wrong", AddressA)).Ok);

        Assert.True((await service.VerifyPasswordAsync(_link, "right", AddressB)).Ok);
        _passwords.Verify(p => p.VerifyPassword("wrong", "link-hash"),
            Times.Exactly(LoginProtectionSettings.DefaultShareLinkMaxAttemptsPerAddress));
    }

    [Fact]
    public async Task AddressLockout_UsesTheConfiguredDuration()
    {
        Configure(LoginProtectionSettings.ShareLinkLockoutMinutesKey, 90);
        var service = Service();
        for (int i = 0; i < LoginProtectionSettings.DefaultShareLinkMaxAttemptsPerAddress; i++)
            await service.VerifyPasswordAsync(_link, "wrong", AddressA);

        var locked = await service.VerifyPasswordAsync(_link, "right", AddressA);

        Assert.True(locked.RetryAfter > TimeSpan.FromMinutes(60), $"retry after {locked.RetryAfter}");
    }

    [Fact]
    public async Task LinkBudget_LocksTheLinkForEveryAddress()
    {
        var service = Service();
        for (int i = 0; i < LoginProtectionSettings.DefaultShareLinkMaxAttemptsPerLink; i++)
            await service.VerifyPasswordAsync(_link, "wrong", $"10.0.{i / 200}.{i % 200 + 1}");

        var fresh = await service.VerifyPasswordAsync(_link, "right", AddressB);

        Assert.False(fresh.Ok);
        Assert.True(fresh.RetryAfter > TimeSpan.Zero);
    }

    [Fact]
    public async Task CorrectPassword_ResetsAddressAndLinkCounters()
    {
        Configure(LoginProtectionSettings.ShareLinkMaxAttemptsPerLinkKey, 6);
        var service = Service();
        for (int i = 0; i < 4; i++)
            await service.VerifyPasswordAsync(_link, "wrong", AddressA);
        Assert.True((await service.VerifyPasswordAsync(_link, "right", AddressA)).Ok);

        // Both counters start over: four more failures neither lock the address (5) nor the link (6).
        for (int i = 0; i < 4; i++)
            await service.VerifyPasswordAsync(_link, "wrong", AddressA);
        Assert.True((await service.VerifyPasswordAsync(_link, "right", AddressA)).Ok);
    }

    [Fact]
    public async Task ConfiguredValues_Apply()
    {
        Configure(LoginProtectionSettings.ShareLinkMaxAttemptsPerAddressKey, 2);
        var service = Service();
        await service.VerifyPasswordAsync(_link, "wrong", AddressA);
        await service.VerifyPasswordAsync(_link, "wrong", AddressA);

        Assert.False((await service.VerifyPasswordAsync(_link, "right", AddressA)).Ok);
    }

    [Fact]
    public async Task WithoutAddress_OnlyTheLinkBudgetApplies()
    {
        var service = Service();
        for (int i = 0; i < LoginProtectionSettings.DefaultShareLinkMaxAttemptsPerAddress; i++)
            await service.VerifyPasswordAsync(_link, "wrong", remoteAddress: null);

        Assert.True((await service.VerifyPasswordAsync(_link, "right", remoteAddress: null)).Ok);
    }

    [Fact]
    public async Task Ipv4MappedAddress_SharesTheBudgetOfItsIpv4Form()
    {
        var service = Service();
        for (int i = 0; i < LoginProtectionSettings.DefaultShareLinkMaxAttemptsPerAddress; i++)
            await service.VerifyPasswordAsync(_link, "wrong", "::ffff:" + AddressA);

        Assert.False((await service.VerifyPasswordAsync(_link, "right", AddressA)).Ok);
    }

    [Fact]
    public async Task LinksAreThrottledIndependently()
    {
        var other = new ShareLink { Id = Guid.NewGuid(), Token = "other", PasswordHash = "link-hash" };
        var service = Service();
        for (int i = 0; i < LoginProtectionSettings.DefaultShareLinkMaxAttemptsPerAddress; i++)
            await service.VerifyPasswordAsync(_link, "wrong", AddressA);

        Assert.True((await service.VerifyPasswordAsync(other, "right", AddressA)).Ok);
    }

    [Fact]
    public async Task LinkWithoutPassword_IsAlwaysOpen()
    {
        var open = new ShareLink { Id = Guid.NewGuid(), Token = "open" };

        Assert.True((await Service().VerifyPasswordAsync(open, null, AddressA)).Ok);
        _passwords.Verify(p => p.VerifyPassword(It.IsAny<string>(), It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task ParallelGuesses_VerifyAtMostTheAddressBudget()
    {
        int verifications = 0;
        _passwords.Setup(p => p.VerifyPassword(It.IsAny<string>(), "link-hash"))
            .Returns(() => { Interlocked.Increment(ref verifications); Thread.Sleep(20); return false; });
        var service = Service();

        await Task.WhenAll(Enumerable.Range(0, 50)
            .Select(_ => Task.Run(() => service.VerifyPasswordAsync(_link, "guess", AddressA))));

        Assert.True(verifications <= LoginProtectionSettings.DefaultShareLinkMaxAttemptsPerAddress,
            $"{verifications} hashes verified");
    }

    [Fact]
    public async Task HashFailure_DoesNotLeakTheReservation()
    {
        _passwords.Setup(p => p.VerifyPassword("boom", "link-hash")).Throws(new InvalidOperationException());
        var service = Service();
        for (int i = 0; i < 20; i++)
            await Assert.ThrowsAsync<InvalidOperationException>(() => service.VerifyPasswordAsync(_link, "boom", AddressA));

        Assert.True((await service.VerifyPasswordAsync(_link, "right", AddressA)).Ok);
    }

    private void Configure(string key, int value)
        => _config.Setup(c => c.GetIntAsync(key, It.IsAny<int>())).ReturnsAsync(value);

    private ShareLinkService Service() => new(
        Mock.Of<IShareLinkRepository>(),
        _config.Object,
        _passwords.Object,
        _throttle,
        Mock.Of<IManagementAuthService>(),
        Mock.Of<IUserContextFactory>(),
        Mock.Of<AuthenticationStateProvider>(),
        new PublicDownloadTicketStore());
}

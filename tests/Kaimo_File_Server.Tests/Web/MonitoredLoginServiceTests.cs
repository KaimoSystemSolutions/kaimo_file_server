using System.Text.Json;
using Kaimo_File_Server.Core.Domain.Notifications;
using Kaimo_File_Server.Core.Repositories;
using Kaimo_File_Server.Core.Security;
using Kaimo_File_Server.Core.Services.Notifications;
using Kaimo_File_Server.Web.Services;
using Microsoft.AspNetCore.Http;
using Moq;
using Xunit;

namespace Kaimo_File_Server.Tests;

/// <summary>
/// The lockout notifications of the monitored login service. A locked account raises
/// <c>security.account_locked</c> once per lock. A locked client address refuses every user
/// name, so it must neither notify the user names tried from it nor look them up; it raises
/// <c>security.address_locked</c> once per lock of that address instead.
/// </summary>
public class MonitoredLoginServiceTests
{
    private static readonly TimeSpan Lock = TimeSpan.FromMinutes(15);

    private readonly Mock<ILoginService> _inner = new();
    private readonly Mock<INotificationPublisher> _notifications = new();
    private readonly Mock<IUserRepository> _users = new();
    private readonly List<NotificationEvent> _published = [];
    private readonly MonitoredLoginService _sut;

    public MonitoredLoginServiceTests()
    {
        _notifications.Setup(n => n.PublishAsync(It.IsAny<NotificationEvent>(), It.IsAny<CancellationToken>()))
            .Callback((NotificationEvent e, CancellationToken _) => _published.Add(e))
            .Returns(Task.CompletedTask);
        _sut = new MonitoredLoginService(
            _inner.Object, new SecurityMonitor(), new HttpContextAccessor(), _notifications.Object, _users.Object);
    }

    // The notified-lock memory is process-wide, so every test uses its own names and addresses.
    private static string UniqueName() => $"user-{Guid.NewGuid():N}";
    private static string UniqueAddress() => $"2001:db8::{Random.Shared.Next(1, 0xffff):x}:{Random.Shared.Next(1, 0xffff):x}";

    private static Dictionary<string, string> Payload(NotificationEvent e)
        => JsonSerializer.Deserialize<Dictionary<string, string>>(e.PayloadJson)!;

    private void ArrangeResult(LoginResult result)
        => _inner.Setup(s => s.AuthenticateAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<string?>()))
            .ReturnsAsync(result);

    [Fact]
    public async Task AccountLockout_NotifiesOncePerLock()
    {
        var name = UniqueName();
        var address = UniqueAddress();
        ArrangeResult(LoginResult.LockedOut(Lock));

        await _sut.AuthenticateAsync(name, "wrong", address);
        await _sut.AuthenticateAsync(name, "wrong", address);

        var notification = Assert.Single(_published);
        Assert.Equal(NotificationEventCatalog.AccountLocked, notification.Type);
    }

    [Fact]
    public async Task AddressLockout_NeitherNotifiesTheUserNamesNorLooksThemUp()
    {
        ArrangeResult(LoginResult.LockedOut(Lock, LockoutScope.Address));
        var address = UniqueAddress();

        foreach (var name in new[] { UniqueName(), UniqueName(), UniqueName() })
        {
            var result = await _sut.AuthenticateAsync(name, "wrong", address);
            Assert.Equal(LoginOutcome.LockedOut, result.Outcome);
        }

        Assert.DoesNotContain(_published, e => e.Type == NotificationEventCatalog.AccountLocked);
        _users.Verify(r => r.GetByUsernameAsync(It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task AddressLockout_NotifiesTheAddressLock_OncePerLock()
    {
        ArrangeResult(LoginResult.LockedOut(Lock, LockoutScope.Address));
        var address = UniqueAddress();

        foreach (var name in new[] { UniqueName(), UniqueName(), UniqueName() })
            await _sut.AuthenticateAsync(name, "wrong", address);

        var notification = Assert.Single(_published);
        Assert.Equal(NotificationEventCatalog.AddressLocked, notification.Type);
        var payload = Payload(notification);
        Assert.Equal(address, payload["remoteAddress"]);
        Assert.Equal("15", payload["lockedMinutes"]);
        Assert.Equal("Web", payload["channel"]);
    }

    [Fact]
    public async Task AddressLockouts_OfDifferentAddresses_AreNotifiedEach()
    {
        ArrangeResult(LoginResult.LockedOut(Lock, LockoutScope.Address));

        await _sut.AuthenticateAsync(UniqueName(), "wrong", UniqueAddress());
        await _sut.AuthenticateAsync(UniqueName(), "wrong", UniqueAddress());

        Assert.Equal(2, _published.Count(e => e.Type == NotificationEventCatalog.AddressLocked));
    }

    [Fact]
    public async Task AddressLockout_WithoutKnownAddress_IsNotNotified()
    {
        ArrangeResult(LoginResult.LockedOut(Lock, LockoutScope.Address));

        await _sut.AuthenticateAsync(UniqueName(), "wrong");

        Assert.Empty(_published);
    }

    [Fact]
    public void AddressLocked_SharesOneDedupKey_AcrossAddresses()
    {
        // The dispatcher drops throttled mails per dedup key: a distributed attack locking many
        // addresses must not send one mail per address to the security administrators.
        var a = NotificationEvents.AddressLocked("203.0.113.7", SecurityChannel.Api, TimeSpan.FromMinutes(30));
        var b = NotificationEvents.AddressLocked("198.51.100.9", SecurityChannel.WebDav, TimeSpan.FromSeconds(90));

        Assert.Equal(a.DedupKey, b.DedupKey);
        Assert.NotNull(a.DedupKey);
        Assert.Equal("{}", a.SubjectUserIdsJson);
        Assert.Equal("API", Payload(a)["channel"]);
        Assert.Equal("30", Payload(a)["lockedMinutes"]);
        Assert.Equal("WebDAV", Payload(b)["channel"]);
        Assert.Equal("2", Payload(b)["lockedMinutes"]);
    }

    [Fact]
    public void AddressLocked_IsInTheCatalog_ForSecurityMonitorHolders()
    {
        var definition = NotificationEventCatalog.Find(NotificationEventCatalog.AddressLocked);

        Assert.NotNull(definition);
        Assert.Equal("security", definition!.Category);
        Assert.Empty(definition.ContextRoles);
        Assert.Contains(definition.DefaultRecipients,
            r => r.Kind == RecipientKind.PermissionHolder && r.Value == "ViewSecurityMonitor");
        Assert.True(definition.DefaultThrottleMinutes > 0);
    }
}

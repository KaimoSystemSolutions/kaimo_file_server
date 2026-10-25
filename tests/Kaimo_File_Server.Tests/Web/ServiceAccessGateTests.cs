using Kaimo_File_Server.Core.Domain.Identity;
using Kaimo_File_Server.Core.Helpers;
using Kaimo_File_Server.Core.Repositories;
using Kaimo_File_Server.Infrastructure.Configuration;
using Kaimo_File_Server.Web.Services;
using Moq;
using Xunit;

namespace Kaimo_File_Server.Tests;

/// <summary>
/// The per-user file-service switches: a service is usable only when it is on globally and
/// for the user, except that members of the Admins group always keep the web login.
/// </summary>
public sealed class ServiceAccessGateTests
{
    private static User NewUser(UserServiceAccess access)
        => new(Guid.NewGuid(), "Alice", "alice", "pw-hash", "nt-hash") { ServiceAccess = access };

    private static ServiceAccessGate Gate(User user, bool isAdmin = false, params (string Key, bool Value)[] globals)
    {
        var config = new Mock<IConfigRepository>();
        config.Setup(c => c.GetBoolAsync(It.IsAny<string>(), It.IsAny<bool>()))
            .ReturnsAsync((string _, bool fallback) => fallback);
        foreach (var (key, value) in globals)
            config.Setup(c => c.GetBoolAsync(key, It.IsAny<bool>())).ReturnsAsync(value);

        var users = new Mock<IUserRepository>();
        users.Setup(r => r.GetGroupsForUserAsync(user.Id)).ReturnsAsync(isAdmin
            ? [new Group(WellKnownGUIDs.GROUP_ADMINS, "Admins")]
            : []);
        return new ServiceAccessGate(config.Object, users.Object);
    }

    [Theory]
    [InlineData(UserServiceAccess.App)]
    [InlineData(UserServiceAccess.Web)]
    public async Task UserFlagSet_GlobalDefaultOn_IsAllowed(UserServiceAccess service)
    {
        var user = NewUser(UserServiceAccess.All);
        Assert.True(await Gate(user).IsAllowedAsync(user, service));
    }

    [Fact]
    public async Task UserFlagCleared_IsDenied()
    {
        var user = NewUser(UserServiceAccess.All & ~UserServiceAccess.App);
        Assert.False(await Gate(user).IsAllowedAsync(user, UserServiceAccess.App));
    }

    [Fact]
    public async Task GlobalOff_IsDenied_EvenWithUserFlag()
    {
        var user = NewUser(UserServiceAccess.All);
        var gate = Gate(user, globals: (ServiceAccessGate.GlobalKey(UserServiceAccess.App), false));
        Assert.False(await gate.IsAllowedAsync(user, UserServiceAccess.App));
    }

    [Fact]
    public async Task WebDav_IsOffGloballyByDefault()
    {
        var user = NewUser(UserServiceAccess.All);
        Assert.False(await Gate(user).IsAllowedAsync(user, UserServiceAccess.WebDav));
    }

    [Fact]
    public async Task Admin_KeepsWebLogin_WhenUserFlagCleared()
    {
        var user = NewUser(UserServiceAccess.None);
        Assert.True(await Gate(user, isAdmin: true).IsAllowedAsync(user, UserServiceAccess.Web));
    }

    [Fact]
    public async Task Admin_KeepsWebLogin_WhenWebOffGlobally()
    {
        var user = NewUser(UserServiceAccess.None);
        var gate = Gate(user, isAdmin: true, (ServiceAccessGate.GlobalKey(UserServiceAccess.Web), false));
        Assert.True(await gate.IsAllowedAsync(user, UserServiceAccess.Web));
    }

    [Fact]
    public async Task Admin_GetsNoExemption_ForOtherServices()
    {
        var user = NewUser(UserServiceAccess.None);
        Assert.False(await Gate(user, isAdmin: true).IsAllowedAsync(user, UserServiceAccess.App));
    }

    [Fact]
    public async Task NonAdmin_WebFlagCleared_IsDenied()
    {
        var user = NewUser(UserServiceAccess.All & ~UserServiceAccess.Web);
        Assert.False(await Gate(user).IsAllowedAsync(user, UserServiceAccess.Web));
    }
}

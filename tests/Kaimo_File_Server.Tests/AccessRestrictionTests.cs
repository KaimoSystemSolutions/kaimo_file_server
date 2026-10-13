using System.Net;
using System.Security.Claims;
using Kaimo_File_Server.Core.Domain.Department;
using Kaimo_File_Server.Core.Domain.Identity;
using Kaimo_File_Server.Core.Repositories;
using Kaimo_File_Server.Core.Security;
using Kaimo_File_Server.Infrastructure.Configuration;
using Kaimo_File_Server.Infrastructure.Services;
using Kaimo_File_Server.Web.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Caching.Memory;
using Moq;
using Xunit;

namespace Kaimo_File_Server.Tests;

/// <summary>
/// Access restriction rules: who a rule covers, what it allows, and that a valid login from a
/// disallowed address is indistinguishable from a wrong password.
/// </summary>
public class AccessRestrictionTests
{
    private static readonly Guid UserId = Guid.NewGuid();
    private static readonly Guid GroupId = Guid.NewGuid();
    private static readonly Guid DepartmentId = Guid.NewGuid();

    private static AccessRestrictionSettings With(params AccessRule[] rules) => new() { Rules = rules.ToList() };

    private static bool Allowed(AccessRestrictionSettings settings, string? address, string? host = null,
        Guid[]? groups = null, Guid[]? departments = null)
        => settings.IsAllowed(UserId, groups ?? [], departments ?? [], address, host);

    [Fact]
    public void UserCoveredByNoRule_IsUnrestricted()
    {
        var settings = With(new AccessRule { UserIds = [Guid.NewGuid()], Networks = ["10.0.0.0/8"] });
        Assert.True(Allowed(settings, "203.0.113.5"));
        Assert.True(Allowed(AccessRestrictionSettings.Default(), null));
    }

    [Fact]
    public void CoveredUser_NeedsMatchingNetworkOrHost()
    {
        var settings = With(new AccessRule
        {
            UserIds = [UserId], Networks = ["10.0.0.0/8", "192.168.1.20"], Hosts = ["intern.example.com"],
        });

        Assert.True(Allowed(settings, "10.20.30.40"));               // CIDR
        Assert.True(Allowed(settings, "192.168.1.20"));              // single address
        Assert.True(Allowed(settings, "::ffff:10.1.1.1"));           // IPv4-mapped IPv6
        Assert.True(Allowed(settings, "203.0.113.5", "INTERN.example.com:8443")); // host, case and port ignored
        Assert.False(Allowed(settings, "203.0.113.5", "files.example.com"));
        Assert.False(Allowed(settings, "192.168.1.21"));
        Assert.False(Allowed(settings, null));                       // unknown address fails closed
    }

    [Fact]
    public void Coverage_ViaGroupOrDepartment_AndDisabledRulesIgnored()
    {
        var settings = With(
            new AccessRule { GroupIds = [GroupId], Networks = ["10.0.0.0/8"] },
            new AccessRule { DepartmentIds = [DepartmentId], Networks = ["172.16.0.0/12"] },
            new AccessRule { UserIds = [UserId], Networks = ["127.0.0.1"], Enabled = false });

        Assert.False(Allowed(settings, "203.0.113.5", groups: [GroupId]));
        Assert.True(Allowed(settings, "10.0.0.1", groups: [GroupId]));
        // Covered by two rules: either one allows.
        Assert.True(Allowed(settings, "172.16.0.1", groups: [GroupId], departments: [DepartmentId]));
        // The disabled rule covers the user directly but is ignored.
        Assert.True(Allowed(settings, "203.0.113.5"));
    }

    [Theory]
    [InlineData("10.0.0.0/8", true)]
    [InlineData("2001:db8::/32", true)]
    [InlineData("192.168.1.20", true)]
    [InlineData("10.0.0.0/33", false)]
    [InlineData("not-an-ip", false)]
    [InlineData("", false)]
    public void TryParseNetwork(string value, bool expected)
        => Assert.Equal(expected, AccessRestrictionSettings.TryParseNetwork(value, out _));

    [Fact]
    public void TryParseNetwork_ToleratesHostBits()
    {
        Assert.True(AccessRestrictionSettings.TryParseNetwork("10.1.2.3/8", out var network));
        Assert.True(network.Contains(System.Net.IPAddress.Parse("10.200.0.1")));
    }

    [Fact]
    public async Task Service_ExpandsDepartmentAncestors()
    {
        var child = new Department("Child", parentDepartmentId: DepartmentId);
        var parent = new Department("Parent") { Id = DepartmentId };
        var user = new User(UserId, "Alice", "alice", "hash", "0123456789ABCDEF0123456789ABCDEF");
        var context = new UserContext(user, [], [], [], [child]);

        var departments = new Mock<IDepartmentRepository>();
        departments.Setup(d => d.GetAncestorChainAsync(child.Id)).ReturnsAsync([parent]);
        var service = new AccessRestrictionService(
            new Mock<IConfigRepository>().Object, new Mock<IUserContextFactory>().Object,
            departments.Object, new MemoryCache(new MemoryCacheOptions()));

        var rules = With(new AccessRule { DepartmentIds = [DepartmentId], Networks = ["10.0.0.0/8"] });
        Assert.False(await service.IsAllowedAsync(context, "203.0.113.5", null, rules));
        Assert.True(await service.IsAllowedAsync(context, "10.0.0.1", null, rules));
    }

    [Fact]
    public async Task Login_FromDisallowedAddress_BehavesLikeWrongPassword()
    {
        var user = new User(UserId, "Alice", "alice", "hash", "0123456789ABCDEF0123456789ABCDEF");
        var users = new Mock<IUserRepository>();
        users.Setup(r => r.GetByUsernameAsync("alice")).ReturnsAsync(user);
        var passwords = new Mock<IPasswordService>();
        passwords.Setup(p => p.VerifyPassword("pw", "hash")).Returns(true);
        var contexts = new Mock<IUserContextFactory>();
        contexts.Setup(f => f.CreateAsync(user)).ReturnsAsync(new UserContext(user, [], [], []));
        var config = new Mock<IConfigRepository>();
        config.Setup(c => c.GetIntAsync(It.IsAny<string>(), It.IsAny<int>())).ReturnsAsync(2);
        config.Setup(c => c.GetAsync(AccessRestrictionSettings.ConfigKey, It.IsAny<AccessRestrictionSettings>()))
            .ReturnsAsync(With(new AccessRule { UserIds = [UserId], Networks = ["10.0.0.0/8"] }));

        var access = new AccessRestrictionService(
            config.Object, contexts.Object, new Mock<IDepartmentRepository>().Object, new MemoryCache(new MemoryCacheOptions()));
        var sut = new CredentialLoginService(
            users.Object, passwords.Object, contexts.Object, new LoginThrottle(TimeProvider.System), config.Object, access);

        // The correct password from a blocked address must not be detectable: it counts as a
        // failure and resets nothing, so with a budget of 2 the next attempt locks this address
        // exactly as two wrong passwords would. Callers show AddressNotAllowed as invalid credentials.
        Assert.Equal(LoginOutcome.AddressNotAllowed, (await sut.AuthenticateAsync("alice", "pw", "203.0.113.5")).Outcome);
        Assert.Equal(LoginOutcome.LockedOut, (await sut.AuthenticateAsync("alice", "wrong", "203.0.113.5")).Outcome);
        Assert.Equal(LoginOutcome.LockedOut, (await sut.AuthenticateAsync("alice", "pw", "203.0.113.5")).Outcome);

        Assert.Equal(LoginOutcome.Success, (await sut.AuthenticateAsync("alice", "pw", "10.0.0.7")).Outcome);
    }

    // API/WebDAV requests of a blocked user get a 403; the Blazor hub instead continues as
    // anonymous, so the browser still reaches the login page instead of a dead UI.
    [Theory]
    [InlineData("/api/v1/files", 403, false)]
    [InlineData("/_blazor/negotiate", 200, true)]
    public async Task Middleware_BlockedUser(string path, int expectedStatus, bool expectNextAnonymous)
    {
        var user = new User(UserId, "Alice", "alice", "hash", "0123456789ABCDEF0123456789ABCDEF");
        var contexts = new Mock<IUserContextFactory>();
        contexts.Setup(f => f.CreateByUserIdAsync(UserId)).ReturnsAsync(new UserContext(user, [], [], []));
        var config = new Mock<IConfigRepository>();
        config.Setup(c => c.GetAsync(AccessRestrictionSettings.ConfigKey, It.IsAny<AccessRestrictionSettings>()))
            .ReturnsAsync(With(new AccessRule { UserIds = [UserId], Networks = ["10.0.0.0/8"] }));
        var access = new AccessRestrictionService(
            config.Object, contexts.Object, new Mock<IDepartmentRepository>().Object, new MemoryCache(new MemoryCacheOptions()));

        var http = new DefaultHttpContext();
        http.Request.Path = path;
        http.Connection.RemoteIpAddress = IPAddress.Parse("203.0.113.5");
        http.User = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, UserId.ToString())], "test"));

        bool? nextSawAuthenticated = null;
        var middleware = new AccessRestrictionMiddleware(ctx =>
        {
            nextSawAuthenticated = ctx.User.Identity?.IsAuthenticated == true;
            return Task.CompletedTask;
        });
        await middleware.InvokeAsync(http, access);

        Assert.Equal(expectedStatus, http.Response.StatusCode);
        Assert.Equal(expectNextAnonymous ? false : null, nextSawAuthenticated);
    }
}

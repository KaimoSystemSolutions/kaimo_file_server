using Kaimo_File_Server.Core.Domain.ClientSync;
using Kaimo_File_Server.Core.Domain.Identity;
using Kaimo_File_Server.Core.Repositories;
using Kaimo_File_Server.Core.Security;
using Kaimo_File_Server.Web.Services;
using Kaimo_File_Server.Web.Services.Api;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Kaimo_File_Server.Tests;

/// <summary>
/// A login over a switched-off file service is refused with
/// <see cref="LoginOutcome.ServiceDisabled"/> (shown to the client like a wrong password),
/// and the client app cannot refresh its token while the app service is off.
/// </summary>
public sealed class ServiceAccessLoginTests
{
    private static User NewUser(UserServiceAccess access)
        => new(Guid.NewGuid(), "Alice", "alice", "pw-hash", "nt-hash") { ServiceAccess = access };

    private static UserContext Context(User user) => new(user, [], [], []);

    private static ServiceAccessLoginService Login(LoginResult inner, string path)
    {
        var login = new Mock<ILoginService>();
        login.Setup(l => l.AuthenticateAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<string?>()))
            .ReturnsAsync(inner);
        var http = new HttpContextAccessor { HttpContext = new DefaultHttpContext() };
        http.HttpContext.Request.Path = path;
        return new ServiceAccessLoginService(login.Object, TestGates.AllGloballyOn(), http);
    }

    [Theory]
    [InlineData("/api/v1/auth/login", UserServiceAccess.App)]
    [InlineData("/dav/", UserServiceAccess.WebDav)]
    [InlineData("/_blazor", UserServiceAccess.Web)]
    public async Task ServiceSwitchedOff_IsRefused(string path, UserServiceAccess service)
    {
        var user = NewUser(UserServiceAccess.All & ~service);
        var result = await Login(LoginResult.ForSuccess(Context(user)), path).AuthenticateAsync("alice", "pw");

        Assert.Equal(LoginOutcome.ServiceDisabled, result.Outcome);
        Assert.Null(result.UserContext);
    }

    [Theory]
    [InlineData("/api/v1/auth/login")]
    [InlineData("/dav/")]
    [InlineData("/_blazor")]
    public async Task OtherServiceSwitchedOff_StillSucceeds(string path)
    {
        // Only SMB is off; none of these transports uses it.
        var user = NewUser(UserServiceAccess.All & ~UserServiceAccess.Smb);
        var result = await Login(LoginResult.ForSuccess(Context(user)), path).AuthenticateAsync("alice", "pw");

        Assert.Equal(LoginOutcome.Success, result.Outcome);
    }

    [Fact]
    public async Task FailedLogin_PassesThroughUnchanged()
    {
        var result = await Login(LoginResult.InvalidCredentials, "/api/v1/auth/login").AuthenticateAsync("alice", "bad");
        Assert.Equal(LoginOutcome.InvalidCredentials, result.Outcome);
    }

    [Fact]
    public async Task NoHttpContext_CountsAsWebLogin()
    {
        var user = NewUser(UserServiceAccess.All & ~UserServiceAccess.Web);
        var login = new Mock<ILoginService>();
        login.Setup(l => l.AuthenticateAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<string?>()))
            .ReturnsAsync(LoginResult.ForSuccess(Context(user)));
        var gate = new ServiceAccessLoginService(login.Object, TestGates.AllGloballyOn(), new HttpContextAccessor());

        Assert.Equal(LoginOutcome.ServiceDisabled, (await gate.AuthenticateAsync("alice", "pw")).Outcome);
    }

    // ─────────────── Client-API token refresh ───────────────

    [Fact]
    public async Task Refresh_AppSwitchedOff_IsRefused_WithoutRotating()
    {
        var user = NewUser(UserServiceAccess.All & ~UserServiceAccess.App);
        var device = new SyncDevice { UserId = user.Id };
        var token = new RefreshToken { UserId = user.Id, DeviceId = device.Id, ExpiresAtUtc = DateTime.UtcNow.AddDays(1) };

        var refreshTokens = new Mock<IRefreshTokenRepository>();
        refreshTokens.Setup(r => r.GetByHashAsync(It.IsAny<string>())).ReturnsAsync(token);
        var devices = new Mock<ISyncDeviceRepository>();
        devices.Setup(d => d.GetByIdAsync(device.Id)).ReturnsAsync(device);
        var contexts = new Mock<IUserContextFactory>();
        contexts.Setup(c => c.CreateByUserIdAsync(user.Id)).ReturnsAsync(Context(user));
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Jwt:Secret"] = "unit-test-jwt-secret-at-least-32-characters-long!!",
        }).Build();

        var service = new ApiTokenService(
            new JwtTokenService(config, NullLogger<JwtTokenService>.Instance),
            refreshTokens.Object, devices.Object, contexts.Object, TimeProvider.System, config,
            NullLogger<ApiTokenService>.Instance, serviceAccess: TestGates.AllGloballyOn());

        var result = await service.RefreshAsync("presented-secret");

        Assert.Equal(RefreshOutcome.ServiceDisabled, result.Outcome);
        refreshTokens.Verify(r => r.RotateAsync(It.IsAny<RefreshToken>(), It.IsAny<RefreshToken>()), Times.Never);
    }
}

using System.Net.Http.Headers;
using System.Security.Claims;
using System.Text;
using System.Text.Encodings.Web;
using Kaimo_File_Server.Core.Domain.ClientSync;
using Kaimo_File_Server.Core.Domain.Identity;
using Kaimo_File_Server.Core.Repositories;
using Kaimo_File_Server.Core.Security;
using Kaimo_File_Server.Infrastructure.Configuration;
using Kaimo_File_Server.Infrastructure.Repositories;
using Kaimo_File_Server.Tests.Infrastructure;
using Kaimo_File_Server.Web.Controllers.WebDav;
using Kaimo_File_Server.Web.Services;
using Kaimo_File_Server.Web.Services.Api;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Xunit;

namespace Kaimo_File_Server.Tests;

/// <summary>
/// A password change, device revocation or logout must invalidate tokens that were
/// already issued: the security stamp rotates with the password, bearer validation
/// rejects web tokens and stale stamps, cached WebDAV logins are dropped, and client
/// API access tokens are short-lived.
/// </summary>
public sealed class TokenInvalidationTests : DatabaseTestBase
{
    private static IConfiguration Config(params (string Key, string Value)[] extra)
    {
        var values = new Dictionary<string, string?>
        {
            ["Jwt:Secret"] = "unit-test-jwt-secret-at-least-32-characters-long!!",
            ["Jwt:Issuer"] = "KaimoFileServer",
        };
        foreach (var (key, value) in extra)
            values[key] = value;
        return new ConfigurationBuilder().AddInMemoryCollection(values).Build();
    }

    private static readonly JwtTokenService Jwt = new(Config(), NullLogger<JwtTokenService>.Instance);

    // ─────────────── Security stamp rotation ───────────────

    [Fact]
    public async Task UpdatePassword_RotatesSecurityStamp()
    {
        var carol = SeedUser("carol");
        var before = (await UserRepo().GetByIdAsync(carol.Id))!.SecurityStamp;

        await UserRepo().UpdatePasswordAsync(carol.Id, "bcrypt-hash", "enc:nt");

        var after = (await UserRepo().GetByIdAsync(carol.Id))!.SecurityStamp;
        Assert.False(string.IsNullOrEmpty(after));
        Assert.NotEqual(before, after);
    }

    [Fact]
    public async Task UpdatePassword_ClearsMustChangePassword_OnlyWhenChangedByUser()
    {
        var carol = SeedUser("carol");
        await using (var db = NewContext())
        {
            await db.Users.Where(u => u.Id == carol.Id)
                .ExecuteUpdateAsync(u => u.SetProperty(x => x.MustChangePassword, true));
        }

        await UserRepo().UpdatePasswordAsync(carol.Id, "admin-reset", "enc:nt");
        Assert.True((await UserRepo().GetByIdAsync(carol.Id))!.MustChangePassword);

        await UserRepo().UpdatePasswordAsync(carol.Id, "self-chosen", "enc:nt", changedByUser: true);
        Assert.False((await UserRepo().GetByIdAsync(carol.Id))!.MustChangePassword);
    }

    // ─────────────── Bearer validation (API + WebDAV) ───────────────

    private static ClaimsPrincipal Principal(string token) => Jwt.ValidateToken(token)!;

    private static (Mock<IUserRepository> Users, Mock<ISyncDeviceRepository> Devices) Repos(User user, SyncDevice? device)
    {
        var users = new Mock<IUserRepository>();
        users.Setup(r => r.GetByIdAsync(user.Id)).ReturnsAsync(user);
        var devices = new Mock<ISyncDeviceRepository>();
        if (device is not null)
            devices.Setup(r => r.GetByIdAsync(device.Id)).ReturnsAsync(device);
        return (users, devices);
    }

    private static User NewUser(bool enabled = true)
        => new(Guid.NewGuid(), "Alice", "alice", "pw-hash", "nt-hash", isEnabled: enabled);

    [Fact]
    public async Task Bearer_DeviceTokenWithCurrentStamp_IsAccepted()
    {
        var user = NewUser();
        var device = new SyncDevice { UserId = user.Id };
        var (users, devices) = Repos(user, device);
        var token = Jwt.GenerateToken(user.Id, "alice", "Alice", [], device.Id, user.SecurityStamp);

        Assert.Null(await BearerTokenValidation.CheckAsync(Principal(token), users.Object, devices.Object));
    }

    [Fact]
    public async Task Bearer_WebLoginToken_IsRejected()
    {
        var user = NewUser();
        var (users, devices) = Repos(user, null);
        var webToken = Jwt.GenerateToken(user.Id, "alice", "Alice", [], securityStamp: user.SecurityStamp);

        Assert.NotNull(await BearerTokenValidation.CheckAsync(Principal(webToken), users.Object, devices.Object));
    }

    [Fact]
    public async Task Bearer_AfterPasswordChange_IsRejected()
    {
        var user = NewUser();
        var device = new SyncDevice { UserId = user.Id };
        var (users, devices) = Repos(user, device);
        var token = Jwt.GenerateToken(user.Id, "alice", "Alice", [], device.Id, user.SecurityStamp);

        user.SecurityStamp = User.NewSecurityStamp(); // password changed

        Assert.NotNull(await BearerTokenValidation.CheckAsync(Principal(token), users.Object, devices.Object));
    }

    [Fact]
    public async Task Bearer_TokenWithoutStamp_IsRejected()
    {
        var user = NewUser();
        var device = new SyncDevice { UserId = user.Id };
        var (users, devices) = Repos(user, device);
        var legacy = Jwt.GenerateToken(user.Id, "alice", "Alice", [], device.Id);

        Assert.NotNull(await BearerTokenValidation.CheckAsync(Principal(legacy), users.Object, devices.Object));
    }

    [Fact]
    public async Task Bearer_RevokedDevice_DisabledUser_OrForeignDevice_AreRejected()
    {
        var user = NewUser();
        var revoked = new SyncDevice { UserId = user.Id, RevokedAtUtc = DateTime.UtcNow };
        var (users, devices) = Repos(user, revoked);
        var token = Jwt.GenerateToken(user.Id, "alice", "Alice", [], revoked.Id, user.SecurityStamp);
        Assert.NotNull(await BearerTokenValidation.CheckAsync(Principal(token), users.Object, devices.Object));

        var disabled = NewUser(enabled: false);
        var device = new SyncDevice { UserId = disabled.Id };
        (users, devices) = Repos(disabled, device);
        token = Jwt.GenerateToken(disabled.Id, "alice", "Alice", [], device.Id, disabled.SecurityStamp);
        Assert.NotNull(await BearerTokenValidation.CheckAsync(Principal(token), users.Object, devices.Object));

        var owner = NewUser();
        var foreign = new SyncDevice { UserId = Guid.NewGuid() };
        (users, devices) = Repos(owner, foreign);
        token = Jwt.GenerateToken(owner.Id, "alice", "Alice", [], foreign.Id, owner.SecurityStamp);
        Assert.NotNull(await BearerTokenValidation.CheckAsync(Principal(token), users.Object, devices.Object));
    }

    // ─────────────── Short client-API access tokens ───────────────

    [Fact]
    public async Task ApiAccessToken_DefaultsToFifteenMinutes_AndCarriesStamp()
    {
        var user = NewUser();
        var service = new ApiTokenService(
            Jwt, Mock.Of<IRefreshTokenRepository>(), Mock.Of<ISyncDeviceRepository>(),
            Mock.Of<IUserContextFactory>(), TimeProvider.System, Config(),
            NullLogger<ApiTokenService>.Instance);

        var issued = await service.IssueAsync(ContextFor(user), Guid.NewGuid());

        Assert.Equal(900, issued.ExpiresInSeconds);
        var principal = Principal(issued.AccessToken);
        Assert.Equal(user.SecurityStamp, principal.FindFirstValue(JwtTokenService.SecurityStampClaim));
        var exp = DateTimeOffset.FromUnixTimeSeconds(long.Parse(principal.FindFirstValue("exp")!));
        Assert.InRange(exp - DateTimeOffset.UtcNow, TimeSpan.FromMinutes(14), TimeSpan.FromMinutes(16));
    }

    // ─────────────── Server-side web logout ───────────────

    [Fact]
    public async Task RevokedWebTokens_RevokeIsIdempotent_AndPruneRemovesExpired()
    {
        var repo = new RevokedWebTokenRepository(DbFactory);
        await repo.RevokeAsync("jti-live", DateTime.UtcNow.AddHours(1));
        await repo.RevokeAsync("jti-live", DateTime.UtcNow.AddHours(1));
        await repo.RevokeAsync("jti-old", DateTime.UtcNow.AddHours(-1));

        Assert.True(await repo.IsRevokedAsync("jti-live"));
        Assert.False(await repo.IsRevokedAsync("jti-other"));
        Assert.Equal(1, await repo.PruneExpiredBeforeAsync(DateTime.UtcNow));
        Assert.True(await repo.IsRevokedAsync("jti-live"));
    }

    // ─────────────── WebDAV Basic login cache ───────────────

    [Fact]
    public async Task WebDavCache_AfterInvalidate_OldPasswordIsVerifiedAgain()
    {
        var user = NewUser();
        var cache = new MemoryCache(new MemoryCacheOptions());
        var login = new Mock<ILoginService>();
        login.Setup(l => l.AuthenticateAsync("alice", "old-password", It.IsAny<string?>()))
            .ReturnsAsync(LoginResult.ForSuccess(ContextFor(user)));

        Assert.True((await AuthenticateWebDavAsync(cache, login.Object, "alice", "old-password")).Succeeded);

        // Password changed: the login service now rejects the old password.
        login.Setup(l => l.AuthenticateAsync("alice", "old-password", It.IsAny<string?>()))
            .ReturnsAsync(LoginResult.InvalidCredentials);

        // Without invalidation the cached login would still be accepted for up to 60 s.
        Assert.True((await AuthenticateWebDavAsync(cache, login.Object, "alice", "old-password")).Succeeded);

        WebDavBasicAuthenticationHandler.Invalidate(cache, user.Id);
        Assert.False((await AuthenticateWebDavAsync(cache, login.Object, "alice", "old-password")).Succeeded);
    }

    [Fact]
    public async Task WebDavCache_NewLoginAfterInvalidate_DoesNotReviveOldPassword()
    {
        var user = NewUser();
        var cache = new MemoryCache(new MemoryCacheOptions());
        var login = new Mock<ILoginService>();
        login.Setup(l => l.AuthenticateAsync("alice", "old-password", It.IsAny<string?>()))
            .ReturnsAsync(LoginResult.ForSuccess(ContextFor(user)));
        await AuthenticateWebDavAsync(cache, login.Object, "alice", "old-password");

        login.Setup(l => l.AuthenticateAsync("alice", "old-password", It.IsAny<string?>()))
            .ReturnsAsync(LoginResult.InvalidCredentials);
        login.Setup(l => l.AuthenticateAsync("alice", "new-password", It.IsAny<string?>()))
            .ReturnsAsync(LoginResult.ForSuccess(ContextFor(user)));
        WebDavBasicAuthenticationHandler.Invalidate(cache, user.Id);

        Assert.True((await AuthenticateWebDavAsync(cache, login.Object, "alice", "new-password")).Succeeded);
        Assert.False((await AuthenticateWebDavAsync(cache, login.Object, "alice", "old-password")).Succeeded);
    }

    private static async Task<AuthenticateResult> AuthenticateWebDavAsync(
        IMemoryCache cache, ILoginService login, string username, string password)
    {
        var services = new ServiceCollection();
        services.AddSingleton(Mock.Of<IConfigRepository>());
        var options = new WebDavOptions(
            services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>(), TimeProvider.System);

        var handler = new WebDavBasicAuthenticationHandler(
            Mock.Of<IOptionsMonitor<AuthenticationSchemeOptions>>(m => m.Get(It.IsAny<string>()) == new AuthenticationSchemeOptions()),
            NullLoggerFactory.Instance, UrlEncoder.Default, login, cache, options);

        var context = new DefaultHttpContext();
        context.Request.Scheme = "https";
        context.Request.Headers.Authorization = new AuthenticationHeaderValue(
            "Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes($"{username}:{password}"))).ToString();

        await handler.InitializeAsync(
            new AuthenticationScheme(WebDavBasicAuthenticationHandler.SchemeName, null,
                typeof(WebDavBasicAuthenticationHandler)),
            context);
        return await handler.AuthenticateAsync();
    }
}

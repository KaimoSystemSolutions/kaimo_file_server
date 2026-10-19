using System.Security.Claims;
using Kaimo_File_Server.Core.Domain.Identity;
using Kaimo_File_Server.Core.Repositories;
using Kaimo_File_Server.Core.Security;
using Kaimo_File_Server.Infrastructure.Configuration;
using Kaimo_File_Server.Web.Services;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.JSInterop;
using Moq;
using Xunit;

namespace Kaimo_File_Server.Tests;

/// <summary>
/// Covers the session behaviour of <see cref="JwtAuthenticationStateProvider"/>: a session cookie
/// still yields an authenticated circuit only while the account is enabled, a user disabled/removed
/// mid-session is dropped to anonymous, and an in-circuit login only counts once the browser has
/// stored the HttpOnly session cookie.
/// </summary>
public class JwtAuthenticationStateProviderTests
{
    private static readonly Guid UserId = Guid.NewGuid();
    private const string Stamp = "stamp-at-login";

    private readonly Mock<IRevokedWebTokenRepository> _revoked = new();
    private readonly List<string> _jsPosts = new();
    private string? _token;
    private bool _cookieStored = true;

    // Set by CreateProvider for Connect().
    private JwtTokenService _jwt = null!;
    private IUserRepository _users = null!;
    private string _currentToken = null!;

    private User? _dbUser;
    private bool _dbThrows;
    private int? _configuredSeconds;

    private JwtAuthenticationStateProvider CreateProvider(bool enabled)
    {
        _dbUser = enabled ? MakeUser(enabled: true) : MakeUser(enabled: false);

        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Jwt:Secret"] = "unit-test-jwt-secret-at-least-32-characters-long!!",
                ["Jwt:Issuer"] = "KaimoFileServer",
            })
            .Build();
        var jwt = new JwtTokenService(config, NullLogger<JwtTokenService>.Instance);
        var token = _token ?? jwt.GenerateToken(UserId, "alice", "Alice", new[] { "User" }, securityStamp: Stamp);
        _jwt = jwt;
        _currentToken = token;

        var repo = new Mock<IUserRepository>();
        repo.Setup(r => r.GetByIdAsync(It.IsAny<Guid>()))
            .Returns(() => _dbThrows
                ? throw new InvalidOperationException("db down")
                : Task.FromResult(_dbUser));

        var services = new ServiceCollection();
        services.AddScoped(_ => repo.Object);
        services.AddSingleton(_revoked.Object);
        if (_configuredSeconds is not null)
        {
            var configRepo = new Mock<IConfigRepository>();
            configRepo.Setup(c => c.GetIntAsync(
                    SessionSecuritySettings.RevalidationSecondsKey, It.IsAny<int>()))
                .Returns(() => Task.FromResult(_configuredSeconds!.Value));
            services.AddScoped(_ => configRepo.Object);
        }
        _users = repo.Object;
        var scopeFactory = services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>();

        var js = new Mock<IJSRuntime>();
        js.Setup(j => j.InvokeAsync<bool>("kaimoSession.post", It.IsAny<object?[]?>()))
            .Returns<string, object?[]?>((_, args) =>
            {
                _jsPosts.Add((string)args![0]!);
                return new ValueTask<bool>(_cookieStored);
            });

        return new JwtAuthenticationStateProvider(
            js.Object, jwt, new WebSessionTicketStore(), scopeFactory,
            NullLogger<JwtAuthenticationStateProvider>.Instance)
        {
            // Keep the background loop from firing on its own — tests drive revalidation explicitly.
            RevalidationInterval = TimeSpan.FromHours(1),
        };
    }

    private static User MakeUser(bool enabled, string stamp = Stamp)
        => new(UserId, "Alice", "alice", "pw-hash", "nt-hash", isEnabled: enabled) { SecurityStamp = stamp };

    private static bool IsAuthenticated(AuthenticationState state)
        => state.User.Identity?.IsAuthenticated == true;

    /// <summary>
    /// Simulates a circuit connecting with the session cookie: the WebSession scheme validates the
    /// token (signature + <see cref="WebSessionValidation"/>; an exception fails authentication) and
    /// the circuit host hands the resulting user to the provider.
    /// </summary>
    private async Task<AuthenticationState> Connect(JwtAuthenticationStateProvider provider)
    {
        var principal = _jwt.ValidateToken(_currentToken);
        string? failure;
        try { failure = await WebSessionValidation.CheckAsync(principal, _users, _revoked.Object); }
        catch { failure = "exception"; }

        var user = failure is null ? principal! : new ClaimsPrincipal(new ClaimsIdentity());
        provider.SetAuthenticationState(Task.FromResult(new AuthenticationState(user)));
        return await provider.GetAuthenticationStateAsync();
    }

    // ─────────────── Session establishment ───────────────

    [Fact]
    public async Task Connect_ValidCookie_EnabledUser_IsAuthenticated()
    {
        using var f = CreateProvider(enabled: true);

        var state = await Connect(f);

        Assert.True(IsAuthenticated(state));
        Assert.Equal("alice", state.User.Identity?.Name);
    }

    [Fact]
    public async Task Connect_ValidCookie_DisabledUser_IsAnonymous()
    {
        using var f = CreateProvider(enabled: false);

        Assert.False(IsAuthenticated(await Connect(f)));
    }

    [Fact]
    public async Task Connect_ValidCookie_DeletedUser_IsAnonymous()
    {
        var f = CreateProvider(enabled: true);
        using var _ = f;
        _dbUser = null; // account removed

        Assert.False(IsAuthenticated(await Connect(f)));
    }

    [Fact]
    public async Task Connect_NoCookie_IsAnonymous()
    {
        using var f = CreateProvider(enabled: true);

        Assert.False(IsAuthenticated(await f.GetAuthenticationStateAsync()));
    }

    [Fact]
    public async Task Reconnect_WithoutValidCookie_InterruptsSession()
    {
        using var f = CreateProvider(enabled: true);
        Assert.True(IsAuthenticated(await Connect(f)));

        AuthenticationState? pushed = null;
        f.AuthenticationStateChanged += task => pushed = task.GetAwaiter().GetResult();

        // Signed out in another tab: the reconnecting hub request carries no valid cookie.
        f.SetAuthenticationState(Task.FromResult(new AuthenticationState(new ClaimsPrincipal(new ClaimsIdentity()))));

        Assert.NotNull(pushed);
        Assert.False(IsAuthenticated(pushed!));
        Assert.False(IsAuthenticated(await f.GetAuthenticationStateAsync()));
    }

    [Fact]
    public async Task Reconnect_AsDifferentUser_InterruptsSession()
    {
        using var f = CreateProvider(enabled: true);
        Assert.True(IsAuthenticated(await Connect(f)));

        // Another tab signed in as someone else and overwrote the shared cookie.
        var other = new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim(ClaimTypes.NameIdentifier, Guid.NewGuid().ToString()), new Claim(ClaimTypes.Name, "bob")],
            "test"));
        f.SetAuthenticationState(Task.FromResult(new AuthenticationState(other)));

        Assert.False(IsAuthenticated(await f.GetAuthenticationStateAsync()));
    }

    [Fact]
    public async Task Reconnect_AsSameUser_KeepsSession()
    {
        using var f = CreateProvider(enabled: true);
        Assert.True(IsAuthenticated(await Connect(f)));

        Assert.True(IsAuthenticated(await Connect(f)));
    }

    [Fact]
    public async Task StoreSessionTokenAsync_CookieStored_AuthenticatesCircuit()
    {
        using var f = CreateProvider(enabled: true);

        Assert.True(await f.StoreSessionTokenAsync(_currentToken));

        Assert.Equal(["/auth/session"], _jsPosts);
        Assert.True(IsAuthenticated(await f.GetAuthenticationStateAsync()));
    }

    [Fact]
    public async Task StoreSessionTokenAsync_CookieRefused_StaysAnonymous()
    {
        _cookieStored = false; // e.g. page served over plain HTTP
        using var f = CreateProvider(enabled: true);

        Assert.False(await f.StoreSessionTokenAsync(_currentToken));

        Assert.False(IsAuthenticated(await f.GetAuthenticationStateAsync()));
    }

    // ─────────────── Periodic revalidation ───────────────

    [Fact]
    public async Task RevalidateOnceAsync_UserBecomesDisabled_InterruptsSession()
    {
        using var f = CreateProvider(enabled: true);
        Assert.True(IsAuthenticated(await Connect(f)));

        AuthenticationState? pushed = null;
        f.AuthenticationStateChanged += task => pushed = task.GetAwaiter().GetResult();

        _dbUser = MakeUser(enabled: false); // admin disables the account
        await f.RevalidateOnceAsync();

        Assert.NotNull(pushed);
        Assert.False(IsAuthenticated(pushed!));                       // circuit dropped to anonymous
        Assert.False(IsAuthenticated(await f.GetAuthenticationStateAsync())); // and stays out
    }

    [Fact]
    public async Task RevalidateOnceAsync_UserStillEnabled_KeepsSession()
    {
        using var f = CreateProvider(enabled: true);
        Assert.True(IsAuthenticated(await Connect(f)));

        bool notified = false;
        f.AuthenticationStateChanged += _ => notified = true;

        await f.RevalidateOnceAsync();

        Assert.False(notified);
        Assert.True(IsAuthenticated(await f.GetAuthenticationStateAsync()));
    }

    [Fact]
    public async Task RevalidateOnceAsync_TransientDbError_KeepsSession()
    {
        using var f = CreateProvider(enabled: true);
        Assert.True(IsAuthenticated(await Connect(f)));

        bool notified = false;
        f.AuthenticationStateChanged += _ => notified = true;

        _dbThrows = true;
        await f.RevalidateOnceAsync();

        Assert.False(notified); // transient failure must not kick the user
    }

    [Fact]
    public async Task RevalidateOnceAsync_NoActiveSession_IsNoOp()
    {
        using var f = CreateProvider(enabled: true);

        bool notified = false;
        f.AuthenticationStateChanged += _ => notified = true;

        await f.RevalidateOnceAsync(); // never established a session

        Assert.False(notified);
    }

    // ─────────────── Security stamp / logout / expiry ───────────────

    [Fact]
    public async Task PasswordChange_NewStamp_RejectsExistingToken()
    {
        using var f = CreateProvider(enabled: true);
        _dbUser = MakeUser(enabled: true, stamp: "stamp-after-password-change");

        Assert.False(IsAuthenticated(await Connect(f)));
    }

    [Fact]
    public async Task PasswordChange_DuringSession_InterruptsOnRevalidation()
    {
        using var f = CreateProvider(enabled: true);
        Assert.True(IsAuthenticated(await Connect(f)));

        _dbUser = MakeUser(enabled: true, stamp: "stamp-after-password-change");
        await f.RevalidateOnceAsync();

        Assert.False(IsAuthenticated(await f.GetAuthenticationStateAsync()));
    }

    [Fact]
    public async Task TokenWithoutStamp_IsRejected()
    {
        var legacy = new JwtTokenService(TestConfig(), NullLogger<JwtTokenService>.Instance)
            .GenerateToken(UserId, "alice", "Alice", new[] { "User" });
        _token = legacy;
        using var f = CreateProvider(enabled: true);

        Assert.False(IsAuthenticated(await Connect(f)));
    }

    [Fact]
    public async Task Logout_RevokesToken_SoItCannotEstablishASessionAgain()
    {
        var revoked = new HashSet<string>();
        _revoked.Setup(r => r.RevokeAsync(It.IsAny<string>(), It.IsAny<DateTime>()))
            .Callback<string, DateTime>((jti, _) => revoked.Add(jti))
            .Returns(Task.CompletedTask);
        _revoked.Setup(r => r.IsRevokedAsync(It.IsAny<string>()))
            .Returns<string>(jti => Task.FromResult(revoked.Contains(jti)));
        _token = new JwtTokenService(TestConfig(), NullLogger<JwtTokenService>.Instance)
            .GenerateToken(UserId, "alice", "Alice", new[] { "User" }, securityStamp: Stamp);

        using (var first = CreateProvider(enabled: true))
        {
            Assert.True(IsAuthenticated(await Connect(first)));
            await first.LogoutAsync();
        }
        Assert.Equal(["/auth/logout"], _jsPosts); // cookie cleared by the endpoint

        // Same cookie replayed (e.g. copied out of the browser before logout).
        using var second = CreateProvider(enabled: true);
        Assert.Single(revoked);
        Assert.False(IsAuthenticated(await Connect(second)));
    }

    [Fact]
    public async Task ExpiredTokenDuringSession_InterruptsOnRevalidation()
    {
        _token = new JwtTokenService(TestConfig(), NullLogger<JwtTokenService>.Instance)
            .GenerateToken(UserId, "alice", "Alice", new[] { "User" },
                securityStamp: Stamp, lifetime: TimeSpan.FromSeconds(3));
        using var f = CreateProvider(enabled: true);
        Assert.True(IsAuthenticated(await Connect(f)));

        await Task.Delay(TimeSpan.FromSeconds(3.5)); // past exp; within the validator's clock skew
        await f.RevalidateOnceAsync();

        Assert.False(IsAuthenticated(await f.GetAuthenticationStateAsync()));
    }

    private static IConfiguration TestConfig() => new ConfigurationBuilder()
        .AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Jwt:Secret"] = "unit-test-jwt-secret-at-least-32-characters-long!!",
            ["Jwt:Issuer"] = "KaimoFileServer",
        })
        .Build();

    // ─────────────── Configurable interval ───────────────

    [Fact]
    public async Task ReadRevalidationIntervalAsync_UsesConfiguredValue()
    {
        _configuredSeconds = 90;
        using var f = CreateProvider(enabled: true);

        Assert.Equal(TimeSpan.FromSeconds(90), await f.ReadRevalidationIntervalAsync());
    }

    [Theory]
    [InlineData(1, SessionSecuritySettings.MinRevalidationSeconds)]     // below min → clamped up
    [InlineData(999999, SessionSecuritySettings.MaxRevalidationSeconds)] // above max → clamped down
    public async Task ReadRevalidationIntervalAsync_ClampsOutOfRangeValues(int configured, int expected)
    {
        _configuredSeconds = configured;
        using var f = CreateProvider(enabled: true);

        Assert.Equal(TimeSpan.FromSeconds(expected), await f.ReadRevalidationIntervalAsync());
    }

    [Fact]
    public async Task ReadRevalidationIntervalAsync_NoConfigStore_FallsBackToDefault()
    {
        _configuredSeconds = null; // no IConfigRepository registered in the scope
        using var f = CreateProvider(enabled: true);
        f.RevalidationInterval = TimeSpan.FromSeconds(42);

        Assert.Equal(TimeSpan.FromSeconds(42), await f.ReadRevalidationIntervalAsync());
    }
}

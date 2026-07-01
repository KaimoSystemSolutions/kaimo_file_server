using System.Security.Claims;
using Kaimo_File_Server.Core.Domain.Identity;
using Kaimo_File_Server.Core.Repositories;
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
/// Covers the session-revalidation behaviour of <see cref="JwtAuthenticationStateProvider"/>: a
/// valid token still yields an authenticated session only while the account is enabled, and a user
/// disabled/removed mid-session is dropped to anonymous (while the token is left in localStorage).
/// </summary>
public class JwtAuthenticationStateProviderTests
{
    private static readonly Guid UserId = Guid.NewGuid();

    private User? _dbUser;
    private bool _dbThrows;

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
        var token = jwt.GenerateToken(UserId, "alice", "Alice", new[] { "User" });

        var repo = new Mock<IUserRepository>();
        repo.Setup(r => r.GetByIdAsync(It.IsAny<Guid>()))
            .Returns(() => _dbThrows
                ? throw new InvalidOperationException("db down")
                : Task.FromResult(_dbUser));

        var services = new ServiceCollection();
        services.AddScoped(_ => repo.Object);
        var scopeFactory = services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>();

        var js = new Mock<IJSRuntime>();
        js.Setup(j => j.InvokeAsync<string?>("localStorage.getItem", It.IsAny<object?[]?>()))
            .Returns(new ValueTask<string?>(token));

        return new JwtAuthenticationStateProvider(
            js.Object, jwt, scopeFactory, NullLogger<JwtAuthenticationStateProvider>.Instance)
        {
            // Keep the background loop from firing on its own — tests drive revalidation explicitly.
            RevalidationInterval = TimeSpan.FromHours(1),
        };
    }

    private static User MakeUser(bool enabled)
        => new(UserId, "Alice", "alice", "pw-hash", "nt-hash", isEnabled: enabled);

    private static bool IsAuthenticated(AuthenticationState state)
        => state.User.Identity?.IsAuthenticated == true;

    // ─────────────── Session establishment ───────────────

    [Fact]
    public async Task GetAuthenticationStateAsync_ValidToken_EnabledUser_IsAuthenticated()
    {
        using var f = CreateProvider(enabled: true);

        var state = await f.GetAuthenticationStateAsync();

        Assert.True(IsAuthenticated(state));
        Assert.Equal("alice", state.User.Identity?.Name);
    }

    [Fact]
    public async Task GetAuthenticationStateAsync_ValidToken_DisabledUser_IsAnonymous()
    {
        using var f = CreateProvider(enabled: false);

        var state = await f.GetAuthenticationStateAsync();

        Assert.False(IsAuthenticated(state));
    }

    [Fact]
    public async Task GetAuthenticationStateAsync_ValidToken_DeletedUser_IsAnonymous()
    {
        var f = CreateProvider(enabled: true);
        using var _ = f;
        _dbUser = null; // account removed

        var state = await f.GetAuthenticationStateAsync();

        Assert.False(IsAuthenticated(state));
    }

    [Fact]
    public async Task GetAuthenticationStateAsync_DbError_AllowsOnValidToken()
    {
        using var f = CreateProvider(enabled: true);
        _dbThrows = true; // transient outage at session start

        var state = await f.GetAuthenticationStateAsync();

        Assert.True(IsAuthenticated(state)); // fail-open on transient DB error
    }

    // ─────────────── Periodic revalidation ───────────────

    [Fact]
    public async Task RevalidateOnceAsync_UserBecomesDisabled_InterruptsSession()
    {
        using var f = CreateProvider(enabled: true);
        Assert.True(IsAuthenticated(await f.GetAuthenticationStateAsync()));

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
        Assert.True(IsAuthenticated(await f.GetAuthenticationStateAsync()));

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
        Assert.True(IsAuthenticated(await f.GetAuthenticationStateAsync()));

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
}

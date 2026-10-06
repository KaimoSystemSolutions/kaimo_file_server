using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using Kaimo_File_Server.Core.Repositories;
using Kaimo_File_Server.Core.Security;
using Kaimo_File_Server.Infrastructure.Configuration;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.JSInterop;

namespace Kaimo_File_Server.Web.Services;

/// <summary>
/// Blazor AuthenticationStateProvider for the web session. The session JWT lives in the
/// HttpOnly <see cref="WebSessionCookie"/> — never in script-readable storage:
///   • When a circuit connects (or reconnects), the <see cref="WebSessionCookie.SchemeName"/>
///     scheme authenticates the Blazor hub request from the cookie, already applying
///     <see cref="WebSessionValidation"/>, and the circuit host hands the resulting user to
///     <see cref="SetAuthenticationState"/>.
///   • A login inside a running circuit (<see cref="StoreSessionTokenAsync"/>) has the page post
///     a one-time ticket to <c>/auth/session</c>, which sets the cookie.
///
/// A valid, unexpired token is not sufficient on its own: when the account is disabled (or deleted),
/// its password changes (security stamp) or the token is signed out after it was issued, the
/// active session must end quickly. The provider therefore re-checks the session periodically for
/// the lifetime of the circuit (<see cref="RevalidationInterval"/>) and drops the circuit to
/// anonymous as soon as the account is no longer active. The cookie is left in place — any attempt
/// to re-establish the session re-runs the same checks, so a disabled user cannot get back in.
/// </summary>
public class JwtAuthenticationStateProvider : AuthenticationStateProvider,
    IHostEnvironmentAuthenticationStateProvider, IDisposable
{
    private readonly IJSRuntime _js;
    private readonly JwtTokenService _jwtService;
    private readonly WebSessionTicketStore _tickets;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<JwtAuthenticationStateProvider> _logger;

    private static readonly AuthenticationState _anonymous =
        new(new ClaimsPrincipal(new ClaimsIdentity()));

    private ClaimsPrincipal? _cachedPrincipal;
    private CancellationTokenSource? _revalidationCts;

    /// <summary>
    /// Fallback interval used when the configured value cannot be read (e.g. no config store
    /// available). The effective interval is read from configuration each tick, so an admin can
    /// change it on the settings page and existing sessions pick it up within one cycle.
    /// </summary>
    internal TimeSpan RevalidationInterval { get; set; } =
        TimeSpan.FromSeconds(SessionSecuritySettings.DefaultRevalidationSeconds);

    public JwtAuthenticationStateProvider(
        IJSRuntime js,
        JwtTokenService jwtService,
        WebSessionTicketStore tickets,
        IServiceScopeFactory scopeFactory,
        ILogger<JwtAuthenticationStateProvider> logger)
    {
        _js = js;
        _jwtService = jwtService;
        _tickets = tickets;
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    public override Task<AuthenticationState> GetAuthenticationStateAsync()
        => Task.FromResult(_cachedPrincipal is null ? _anonymous : new AuthenticationState(_cachedPrincipal));

    /// <summary>
    /// Called by the circuit host with the user of the Blazor hub connection — on connect and on
    /// every reconnect. That user was authenticated from the session cookie and validated by
    /// <see cref="WebSessionValidation"/>; an absent or rejected cookie yields an anonymous user.
    /// </summary>
    public void SetAuthenticationState(Task<AuthenticationState> authenticationStateTask)
    {
        _ = ApplyHostStateAsync(authenticationStateTask);
    }

    private async Task ApplyHostStateAsync(Task<AuthenticationState> authenticationStateTask)
    {
        ClaimsPrincipal user;
        try
        {
            user = (await authenticationStateTask).User;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not read the authentication state of the Blazor connection");
            return;
        }

        if (user.Identity?.IsAuthenticated == true && _cachedPrincipal is not null
            && _cachedPrincipal.FindFirstValue(ClaimTypes.NameIdentifier) != user.FindFirstValue(ClaimTypes.NameIdentifier))
        {
            // Reconnected as a different account (another tab signed in over the shared cookie).
            // The circuit's UI state belongs to the previous user, so it must not silently switch.
            _logger.LogInformation("Blazor connection now carries a different user — interrupting session");
            InterruptSession();
        }
        else if (user.Identity?.IsAuthenticated == true)
        {
            _logger.LogDebug("Web session established for: {Username}", user.Identity.Name);
            _cachedPrincipal = user;
            StartRevalidationLoop();
            NotifyAuthenticationStateChanged(Task.FromResult(new AuthenticationState(user)));
        }
        else if (_cachedPrincipal is not null)
        {
            // Reconnected without a valid cookie (signed out in another tab, expired, revoked).
            InterruptSession();
        }
    }

    /// <summary>
    /// Starts a session inside the running circuit: has the browser exchange a one-time ticket for
    /// the HttpOnly session cookie, then switches the circuit to the token's user. Returns false
    /// when the cookie could not be set (e.g. the page is not served over HTTPS) — the session
    /// would not survive a reload then, so the caller treats it as a failed sign-in.
    /// </summary>
    public async Task<bool> StoreSessionTokenAsync(string token)
    {
        var principal = _jwtService.ValidateToken(token);
        if (principal is null
            || !long.TryParse(principal.FindFirst(JwtRegisteredClaimNames.Exp)?.Value, out var exp))
            return false;

        var ticket = _tickets.Issue(new WebSessionTicket(token, DateTimeOffset.FromUnixTimeSeconds(exp)));
        bool stored;
        try
        {
            stored = await _js.InvokeAsync<bool>("kaimoSession.post", "/auth/session", new { ticket });
        }
        catch (Exception ex) when (ex is JSException or JSDisconnectedException
                                       or TaskCanceledException or InvalidOperationException)
        {
            _logger.LogWarning("Browser did not complete the session handoff ({Reason})", ex.GetType().Name);
            stored = false;
        }

        if (!stored)
        {
            _logger.LogWarning("The session cookie could not be set for {Username}", principal.Identity?.Name);
            return false;
        }

        _logger.LogInformation("Login successful for: {Username}", principal.Identity?.Name);
        _cachedPrincipal = principal;
        StartRevalidationLoop();
        NotifyAuthenticationStateChanged(Task.FromResult(new AuthenticationState(principal)));
        return true;
    }

    public async Task LogoutAsync()
    {
        _logger.LogInformation("Logout performed");
        StopRevalidationLoop();
        await RevokeCurrentTokenAsync(_cachedPrincipal);
        try { await _js.InvokeAsync<bool>("kaimoSession.post", "/auth/logout", null); } catch { }
        _cachedPrincipal = null;
        NotifyAuthenticationStateChanged(Task.FromResult(_anonymous));
    }

    // ─────────────────────────── Account revalidation ───────────────────────────

    /// <summary>
    /// Re-checks the current session's account exactly once and interrupts the session if the
    /// account is no longer active. Exposed for the periodic loop and for tests.
    /// </summary>
    internal async Task RevalidateOnceAsync()
    {
        var principal = _cachedPrincipal;
        if (principal is null)
            return;

        string? failure;
        try
        {
            using var scope = _scopeFactory.CreateScope();
            failure = await WebSessionValidation.CheckAsync(
                principal,
                scope.ServiceProvider.GetRequiredService<IUserRepository>(),
                scope.ServiceProvider.GetRequiredService<IRevokedWebTokenRepository>());
        }
        catch (Exception ex)
        {
            // Transient failure (e.g. DB down): keep the session; a later tick will catch a real
            // deactivation once the database is reachable again.
            _logger.LogWarning(ex, "Session revalidation check failed transiently; keeping session");
            return;
        }

        if (failure is not null)
        {
            _logger.LogInformation(
                "Session of {User} is no longer valid ({Reason}) — interrupting session",
                principal.Identity?.Name, failure);
            InterruptSession();
        }
    }

    /// <summary>
    /// Records the session token as signed out on the server, so a copy of the cookie cannot
    /// establish a session again before it expires.
    /// </summary>
    private async Task RevokeCurrentTokenAsync(ClaimsPrincipal? principal)
    {
        var jti = principal?.FindFirst(JwtRegisteredClaimNames.Jti)?.Value;
        if (string.IsNullOrEmpty(jti)
            || !long.TryParse(principal!.FindFirst(JwtRegisteredClaimNames.Exp)?.Value, out var exp))
            return;

        try
        {
            using var scope = _scopeFactory.CreateScope();
            await scope.ServiceProvider.GetRequiredService<IRevokedWebTokenRepository>()
                .RevokeAsync(jti, DateTimeOffset.FromUnixTimeSeconds(exp).UtcDateTime);
        }
        catch (Exception ex)
        {
            // The local sign-out still proceeds; the token then only expires naturally.
            _logger.LogWarning(ex, "Could not revoke the web token on logout");
        }
    }

    /// <summary>Drops the session to anonymous. The cookie is left in place on purpose.</summary>
    private void InterruptSession()
    {
        _cachedPrincipal = null;
        StopRevalidationLoop();
        NotifyAuthenticationStateChanged(Task.FromResult(_anonymous));
    }

    private void StartRevalidationLoop()
    {
        if (_revalidationCts is not null)
            return; // already running for this session

        var cts = new CancellationTokenSource();
        _revalidationCts = cts;
        _ = RunRevalidationLoopAsync(cts.Token);
    }

    private void StopRevalidationLoop()
    {
        var cts = _revalidationCts;
        _revalidationCts = null;
        if (cts is null) return;
        try { cts.Cancel(); } catch { }
        cts.Dispose();
    }

    private async Task RunRevalidationLoopAsync(CancellationToken ct)
    {
        try
        {
            var period = await ReadRevalidationIntervalAsync();
            using var timer = new PeriodicTimer(period);
            while (await timer.WaitForNextTickAsync(ct))
            {
                await RevalidateOnceAsync();

                // Pick up an admin's change to the configured interval for the next cycle.
                var next = await ReadRevalidationIntervalAsync();
                if (next != period)
                {
                    period = next;
                    timer.Period = next;
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Loop cancelled on logout / session interrupt / dispose — expected.
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Session revalidation loop terminated unexpectedly");
        }
    }

    /// <summary>
    /// Reads the configured revalidation interval (clamped to a sane range). Falls back to
    /// <see cref="RevalidationInterval"/> if no config store is available or the read fails.
    /// </summary>
    internal async Task<TimeSpan> ReadRevalidationIntervalAsync()
    {
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var config = scope.ServiceProvider.GetService<IConfigRepository>();
            if (config is null)
                return RevalidationInterval;

            int seconds = await config.GetIntAsync(
                SessionSecuritySettings.RevalidationSecondsKey,
                (int)RevalidationInterval.TotalSeconds);

            return TimeSpan.FromSeconds(SessionSecuritySettings.ClampRevalidationSeconds(seconds));
        }
        catch
        {
            return RevalidationInterval;
        }
    }

    public void Dispose() => StopRevalidationLoop();
}

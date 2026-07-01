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
/// Blazor AuthenticationStateProvider that reads and validates the JWT token directly from
/// localStorage (via IJSRuntime).
///
/// A valid, unexpired token is not sufficient on its own: an account that is disabled (or deleted)
/// after the token was issued must lose its active session quickly. To that end the provider
/// re-checks the account's <c>IsEnabled</c> flag in the database
///   • once when a session is (re)established in <see cref="GetAuthenticationStateAsync"/>, and
///   • periodically for the lifetime of the circuit (<see cref="RevalidationInterval"/>).
/// When the account is no longer active the circuit drops to anonymous immediately. The token
/// itself is intentionally left in localStorage — the session is interrupted, and any attempt to
/// re-establish it re-runs the same DB check, so a disabled user cannot get back in.
/// </summary>
public class JwtAuthenticationStateProvider : AuthenticationStateProvider, IDisposable
{
    private readonly IJSRuntime _js;
    private readonly JwtTokenService _jwtService;
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
        IServiceScopeFactory scopeFactory,
        ILogger<JwtAuthenticationStateProvider> logger)
    {
        _js = js;
        _jwtService = jwtService;
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    public override async Task<AuthenticationState> GetAuthenticationStateAsync()
    {
        if (_cachedPrincipal is not null)
            return new AuthenticationState(_cachedPrincipal);

        try
        {
            var token = await _js.InvokeAsync<string?>("localStorage.getItem", "auth_token");

            if (string.IsNullOrEmpty(token))
            {
                _logger.LogDebug("No token found in localStorage");
                return _anonymous;
            }

            var principal = _jwtService.ValidateToken(token);

            if (principal is null)
            {
                _logger.LogInformation("Token invalid or expired, removing it");
                try { await _js.InvokeVoidAsync("localStorage.removeItem", "auth_token"); } catch { }
                return _anonymous;
            }

            // A still-valid token is not enough: an account disabled/removed since the token was
            // issued must not be able to (re)establish a session. Transient DB errors fail open —
            // the app is unusable without the DB anyway, and the periodic loop catches a real
            // deactivation once the DB is reachable again.
            try
            {
                if (!await IsAccountActiveAsync(principal))
                {
                    _logger.LogInformation(
                        "Token valid but account disabled/removed — denying session for {User}",
                        principal.Identity?.Name);
                    return _anonymous;
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "Could not verify account status at session start; allowing on valid token");
            }

            _logger.LogDebug("Token valid for: {Username}", principal.Identity?.Name);
            _cachedPrincipal = principal;
            StartRevalidationLoop();
            return new AuthenticationState(principal);
        }
        catch (InvalidOperationException)
        {
            // JS interop not available (should not happen with prerender: false)
            return _anonymous;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error while loading the auth state");
            return _anonymous;
        }
    }

    public async Task StoreTokenInLocalStorageAsync(string token)
    {
        await _js.InvokeVoidAsync("localStorage.setItem", "auth_token", token);

        var principal = _jwtService.ValidateToken(token);
        if (principal is not null)
        {
            _logger.LogInformation("Login successful for: {Username}", principal.Identity?.Name);
            _cachedPrincipal = principal;
            StartRevalidationLoop();
            NotifyAuthenticationStateChanged(
                Task.FromResult(new AuthenticationState(principal)));
        }
        else
        {
            _cachedPrincipal = null;
            NotifyAuthenticationStateChanged(Task.FromResult(_anonymous));
        }
    }

    public async Task LogoutAsync()
    {
        _logger.LogInformation("Logout performed");
        StopRevalidationLoop();
        try { await _js.InvokeVoidAsync("localStorage.removeItem", "auth_token"); } catch { }
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

        bool active;
        try
        {
            active = await IsAccountActiveAsync(principal);
        }
        catch (Exception ex)
        {
            // Transient failure (e.g. DB down): keep the session; a later tick will catch a real
            // deactivation once the database is reachable again.
            _logger.LogWarning(ex, "Session revalidation check failed transiently; keeping session");
            return;
        }

        if (!active)
        {
            _logger.LogInformation(
                "Account for {User} is disabled/removed — interrupting session", principal.Identity?.Name);
            InterruptSession();
        }
    }

    /// <summary>Looks up the account by its NameIdentifier claim and returns whether it exists and is enabled.</summary>
    private async Task<bool> IsAccountActiveAsync(ClaimsPrincipal principal)
    {
        var idValue = principal.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        if (!Guid.TryParse(idValue, out var userId))
            return false; // malformed/foreign token → not a valid session

        using var scope = _scopeFactory.CreateScope();
        var users = scope.ServiceProvider.GetRequiredService<IUserRepository>();
        var user = await users.GetByIdAsync(userId);
        return user is { IsEnabled: true };
    }

    /// <summary>Drops the session to anonymous. The token is left in localStorage on purpose.</summary>
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

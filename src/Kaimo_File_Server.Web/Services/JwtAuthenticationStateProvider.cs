using System.Security.Claims;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.Extensions.Logging;
using Microsoft.JSInterop;

namespace Kaimo_File_Server.Web.Services;

/// <summary>
/// Blazor AuthenticationStateProvider that reads and validates the JWT token
/// directly from localStorage (via IJSRuntime).
/// </summary>
public class JwtAuthenticationStateProvider : AuthenticationStateProvider
{
    private readonly IJSRuntime _js;
    private readonly JwtTokenService _jwtService;
    private readonly ILogger<JwtAuthenticationStateProvider> _logger;

    private static readonly AuthenticationState _anonymous =
        new(new ClaimsPrincipal(new ClaimsIdentity()));

    private ClaimsPrincipal? _cachedPrincipal;

    public JwtAuthenticationStateProvider(
        IJSRuntime js,
        JwtTokenService jwtService,
        ILogger<JwtAuthenticationStateProvider> logger)
    {
        _js = js;
        _jwtService = jwtService;
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

            _logger.LogDebug("Token valid for: {Username}", principal.Identity?.Name);
            _cachedPrincipal = principal;
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
        try { await _js.InvokeVoidAsync("localStorage.removeItem", "auth_token"); } catch { }
        _cachedPrincipal = null;
        NotifyAuthenticationStateChanged(Task.FromResult(_anonymous));
    }
}
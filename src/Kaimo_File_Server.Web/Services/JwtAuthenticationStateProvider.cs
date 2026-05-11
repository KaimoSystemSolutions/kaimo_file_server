using System.Security.Claims;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Components.Server.ProtectedBrowserStorage;

namespace Kaimo_File_Server.Web.Services;

/// <summary>
/// Blazor AuthenticationStateProvider der den JWT-Token aus dem
/// ProtectedLocalStorage liest und validiert.
/// 
/// Flow:
/// 1. Login-Page ruft LoginAsync(token) auf → speichert Token
/// 2. Bei jedem Circuit-Start wird GetAuthenticationStateAsync aufgerufen
/// 3. Token wird validiert → ClaimsPrincipal erzeugt
/// 4. Logout löscht den Token
/// </summary>
public class JwtAuthenticationStateProvider : AuthenticationStateProvider
{
    private readonly ProtectedLocalStorage _localStorage;
    private readonly JwtTokenService _jwtService;

    private static readonly AuthenticationState _anonymous =
        new(new ClaimsPrincipal(new ClaimsIdentity()));

    public JwtAuthenticationStateProvider(
        ProtectedLocalStorage localStorage,
        JwtTokenService jwtService)
    {
        _localStorage = localStorage;
        _jwtService = jwtService;
    }

    public override async Task<AuthenticationState> GetAuthenticationStateAsync()
    {
        try
        {
            var result = await _localStorage.GetAsync<string>("auth_token");
            if (!result.Success || string.IsNullOrEmpty(result.Value))
                return _anonymous;

            var principal = _jwtService.ValidateToken(result.Value);
            if (principal is null)
                return _anonymous;

            return new AuthenticationState(principal);
        }
        catch
        {
            // ProtectedLocalStorage wirft bei Prerendering
            return _anonymous;
        }
    }

    public async Task LoginAsync(string token)
    {
        await _localStorage.SetAsync("auth_token", token);
        var principal = _jwtService.ValidateToken(token);
        var state = principal is not null
            ? new AuthenticationState(principal)
            : _anonymous;
        NotifyAuthenticationStateChanged(Task.FromResult(state));
    }

    public async Task LogoutAsync()
    {
        await _localStorage.DeleteAsync("auth_token");
        NotifyAuthenticationStateChanged(Task.FromResult(_anonymous));
    }
}

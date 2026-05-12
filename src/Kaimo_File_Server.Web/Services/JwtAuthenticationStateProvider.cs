using System.Security.Claims;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.JSInterop;

namespace Kaimo_File_Server.Web.Services;

/// <summary>
/// Blazor AuthenticationStateProvider der den JWT-Token direkt aus
/// localStorage (via IJSRuntime) liest und validiert.
///
/// Warum nicht ProtectedLocalStorage?
/// - ProtectedLocalStorage verschlüsselt mit DataProtection-Keys
/// - Bei Container-Restart oder Key-Rotation kann der Token nicht
///   mehr entschlüsselt werden → User wird ausgeloggt
/// - Der JWT ist selbst signiert/validiert, braucht keine Extra-Verschlüsselung
/// </summary>
public class JwtAuthenticationStateProvider : AuthenticationStateProvider
{
    private readonly IJSRuntime _js;
    private readonly JwtTokenService _jwtService;

    private static readonly AuthenticationState _anonymous =
        new(new ClaimsPrincipal(new ClaimsIdentity()));

    private ClaimsPrincipal? _cachedPrincipal;

    public JwtAuthenticationStateProvider(
        IJSRuntime js,
        JwtTokenService jwtService)
    {
        _js = js;
        _jwtService = jwtService;
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
                Console.WriteLine("[AUTH] Kein Token im localStorage gefunden");
                return _anonymous;
            }

            Console.WriteLine("[AUTH] Token gefunden, validiere...");
            var principal = _jwtService.ValidateToken(token);

            if (principal is null)
            {
                Console.WriteLine("[AUTH] Token ungültig oder abgelaufen");
                try { await _js.InvokeVoidAsync("localStorage.removeItem", "auth_token"); } catch { }
                return _anonymous;
            }

            Console.WriteLine($"[AUTH] Token valid für: {principal.Identity?.Name}");
            _cachedPrincipal = principal;
            return new AuthenticationState(principal);
        }
        catch (InvalidOperationException ex)
        {
            // Sollte mit prerender: false nicht passieren
            Console.WriteLine($"[AUTH] JS Interop nicht verfügbar: {ex.Message}");
            return _anonymous;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[AUTH] Unerwarteter Fehler: {ex.Message}");
            return _anonymous;
        }
    }

    public async Task LoginAsync(string token)
    {
        Console.WriteLine("[AUTH] Speichere Token in localStorage...");
        await _js.InvokeVoidAsync("localStorage.setItem", "auth_token", token);

        var principal = _jwtService.ValidateToken(token);
        if (principal is not null)
        {
            Console.WriteLine($"[AUTH] Login erfolgreich für: {principal.Identity?.Name}");
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
        Console.WriteLine("[AUTH] Logout, lösche Token...");
        try { await _js.InvokeVoidAsync("localStorage.removeItem", "auth_token"); } catch { }
        _cachedPrincipal = null;
        NotifyAuthenticationStateChanged(Task.FromResult(_anonymous));
    }
}
using Kaimo_File_Server.Core.Repositories;
using Kaimo_File_Server.Core.Security;
using Kaimo_File_Server.Web.Services;

namespace Kaimo_File_Server.Web.Components.ViewModels;

public class LoginViewModel
{
    private readonly IUserRepository _userRepo;
    private readonly IUserContextFactory _userContextFactory;
    private readonly IPasswordService _passwordService;
    private readonly JwtTokenService _jwtService;
    private readonly JwtAuthenticationStateProvider _authState;

    public LoginViewModel(
        IUserRepository userRepo,
        IUserContextFactory userContextFactory,
        IPasswordService passwordService,
        JwtTokenService jwtService,
        JwtAuthenticationStateProvider authState)
    {
        _userRepo = userRepo;
        _userContextFactory = userContextFactory;
        _passwordService = passwordService;
        _jwtService = jwtService;
        _authState = authState;
    }

    // ── State ──

    public string Username { get; set; } = "";
    public string Password { get; set; } = "";
    public bool IsLoading { get; private set; }
    public string? ErrorMessage { get; private set; }
    public bool IsAuthenticated { get; private set; }

    // ── Commands ──

    public async Task<bool> LoginAsync()
    {
        try
        {
            IsLoading = true;
            ErrorMessage = null;

            if (string.IsNullOrWhiteSpace(Username) || string.IsNullOrWhiteSpace(Password))
            {
                ErrorMessage = "Benutzername und Passwort erforderlich.";
                return false;
            }

            var user = await _userRepo.GetByUsernameAsync(Username.Trim());
            if (user is null)
            {
                ErrorMessage = "Benutzername oder Passwort falsch.";
                return false;
            }

            if (!_passwordService.VerifyPassword(Password, user.PasswordHash))
            {
                ErrorMessage = "Benutzername oder Passwort falsch.";
                return false;
            }

            Console.WriteLine($"[LOGIN] Password OK für {user.Username}, lade Rollen...");
            var userContext = await _userContextFactory.CreateAsync(user);
            var roleNames = userContext.Roles.Select(r => r.Name);

            Console.WriteLine($"[LOGIN] Rollen: {string.Join(", ", roleNames)}, generiere Token...");
            var token = _jwtService.GenerateToken(user.Id, user.Username, user.Name, roleNames);
            Console.WriteLine($"[LOGIN] Token generiert, speichere...");
            await _authState.LoginAsync(token);
            Console.WriteLine($"[LOGIN] Token gespeichert, redirect...");
            IsAuthenticated = true;
            return true;
        }
        catch (Exception ex)
        {
            ErrorMessage = $"Anmeldung fehlgeschlagen: {ex.Message}";
            return false;
        }
        finally
        {
            IsLoading = false;
        }
    }

    public async Task LogoutAsync()
    {
        await _authState.LogoutAsync();
        IsAuthenticated = false;
        Username = "";
        Password = "";
    }
}

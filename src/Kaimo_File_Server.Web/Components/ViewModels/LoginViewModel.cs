using Kaimo_File_Server.Core.Repositories;
using Kaimo_File_Server.Core.Security;
using Kaimo_File_Server.Web.Services;
using Microsoft.Extensions.Logging;

namespace Kaimo_File_Server.Web.Components.ViewModels;

public class LoginViewModel
{
    private readonly IUserRepository _userRepo;
    private readonly IUserContextFactory _userContextFactory;
    private readonly IPasswordService _passwordService;
    private readonly JwtTokenService _jwtService;
    private readonly JwtAuthenticationStateProvider _authState;
    private readonly ILogger<LoginViewModel> _logger;

    public LoginViewModel(
        IUserRepository userRepo,
        IUserContextFactory userContextFactory,
        IPasswordService passwordService,
        JwtTokenService jwtService,
        JwtAuthenticationStateProvider authState,
        ILogger<LoginViewModel> logger)
    {
        _userRepo = userRepo;
        _userContextFactory = userContextFactory;
        _passwordService = passwordService;
        _jwtService = jwtService;
        _authState = authState;
        _logger = logger;
    }

    // -- State --

    public string Username { get; set; } = "";
    public string Password { get; set; } = "";
    public bool IsLoading { get; private set; }
    public string? ErrorMessage { get; private set; }
    public bool IsAuthenticated { get; private set; }

    // -- Commands --

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

            if (!user.IsEnabled)
            {
                _logger.LogWarning("Login abgelehnt – Konto deaktiviert: {Username}", user.Username);
                ErrorMessage = "Dieses Konto ist deaktiviert.";
                return false;
            }

            _logger.LogInformation("Login erfolgreich für Benutzer {Username}", user.Username);

            _logger.LogInformation("Login erfolgreich für Benutzer {Username}", user.Username);

            var userContext = await _userContextFactory.CreateAsync(user);
            var roleNames = userContext.Roles.Select(r => r.Name);

            var token = _jwtService.GenerateToken(user.Id, user.Username, user.Name, roleNames);
            await _authState.StoreTokenInLocalStorageAsync(token);

            // Passwort sofort aus dem Speicher entfernen
            Password = "";
            IsAuthenticated = true;
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Login fehlgeschlagen für Benutzer {Username}", Username);
            ErrorMessage = "Anmeldung fehlgeschlagen. Bitte versuchen Sie es erneut.";
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
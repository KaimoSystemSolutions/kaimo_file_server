using Kaimo_File_Server.Core.Security;
using Kaimo_File_Server.Web.Services;
using Microsoft.Extensions.Logging;
using Kaimo_File_Server.Core.Language;

namespace Kaimo_File_Server.Web.Components.ViewModels;

public class LoginViewModel
{
    private readonly ILoginService _loginService;
    private readonly JwtTokenService _jwtService;
    private readonly JwtAuthenticationStateProvider _authState;
    private readonly ILogger<LoginViewModel> _logger;

    public LoginViewModel(
        ILoginService loginService,
        JwtTokenService jwtService,
        JwtAuthenticationStateProvider authState,
        ILogger<LoginViewModel> logger)
    {
        _loginService = loginService;
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
                ErrorMessage = Resources.Web_Login_CredentialsRequired;
                return false;
            }

            // Credential verification, brute-force lockout, and enumeration
            // resistance all live in the login service — the view model only
            // maps the outcome to UI state.
            var result = await _loginService.AuthenticateAsync(Username, Password);

            switch (result.Outcome)
            {
                case LoginOutcome.Success:
                    var context = result.UserContext!;
                    _logger.LogInformation("Login successful for user {Username}", context.User.Username);

                    var roleNames = context.Roles.Select(r => r.Name);
                    var token = _jwtService.GenerateToken(
                        context.User.Id, context.User.Username, context.User.Name, roleNames);
                    await _authState.StoreTokenInLocalStorageAsync(token);

                    // Remove the password from memory immediately.
                    Password = "";
                    IsAuthenticated = true;
                    return true;

                case LoginOutcome.AccountDisabled:
                    _logger.LogWarning("Login abgelehnt – Konto deaktiviert: {Username}", Username);
                    ErrorMessage = Resources.Web_Login_AccountDisabled;
                    return false;

                case LoginOutcome.LockedOut:
                    var minutes = Math.Max(1, (int)Math.Ceiling(result.RetryAfter.TotalMinutes));
                    _logger.LogWarning(
                        "Login gesperrt (Brute-Force-Schutz) für {Username}, erneut in {Minutes} min",
                        Username, minutes);
                    ErrorMessage = string.Format(Resources.Web_Login_TooManyAttempts, minutes);
                    return false;

                default: // InvalidCredentials
                    ErrorMessage = Resources.Web_Login_InvalidCredentials;
                    return false;
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Login failed for user {Username}", Username);
            ErrorMessage = Resources.Web_Login_Failed;
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

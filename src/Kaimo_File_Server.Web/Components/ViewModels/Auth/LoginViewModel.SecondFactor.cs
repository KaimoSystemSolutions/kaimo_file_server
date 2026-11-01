using Kaimo_File_Server.Core.Domain.Identity;
using Kaimo_File_Server.Core.Language;
using Kaimo_File_Server.Core.Security;
using Kaimo_File_Server.Web.Services.SecondFactor;

namespace Kaimo_File_Server.Web.Components.ViewModels;

/// <summary>Where the login page is.</summary>
public enum LoginStage
{
    Credentials,

    /// <summary>Password accepted; waiting for the TOTP code or a recovery code.</summary>
    SecondFactor,

    /// <summary>Password accepted; the policy requires a second factor that is set up here first.</summary>
    Enrollment,

    /// <summary>Enrollment done; the recovery codes are shown once before the session starts.</summary>
    RecoveryCodes,
}

public partial class LoginViewModel
{
    /// <summary>How long the second step may take after the password was accepted.</summary>
    public static readonly TimeSpan PendingLoginLifetime = TimeSpan.FromMinutes(5);

    /// <summary>
    /// How long a forced enrollment may take, counted from when it is shown: the user may first
    /// have to install an authenticator app.
    /// </summary>
    public static readonly TimeSpan EnrollmentLifetime = TimeSpan.FromMinutes(15);

    /// <summary>
    /// The account whose password was accepted. Only the id and the security stamp are kept;
    /// the user is re-read for every step, so a disabled account or a password reset in
    /// between ends the pending sign-in.
    /// </summary>
    private sealed record PendingLogin(Guid UserId, string SecurityStamp, DateTimeOffset ExpiresAt);

    private PendingLogin? _pending;

    public LoginStage Stage { get; private set; } = LoginStage.Credentials;

    /// <summary>The TOTP code or recovery code being entered.</summary>
    public string Code { get; set; } = "";

    /// <summary>The enrollment shown in <see cref="LoginStage.Enrollment"/>.</summary>
    public SecondFactorEnrollment? Enrollment { get; private set; }

    /// <summary>The recovery codes shown once in <see cref="LoginStage.RecoveryCodes"/>.</summary>
    public IReadOnlyList<string>? RecoveryCodes { get; private set; }

    /// <summary>After the password: sign in, or move on to the second-factor step the account needs.</summary>
    private async Task<bool> ContinueAfterPasswordAsync(UserContext context)
    {
        var step = _secondFactorPolicy is null || _secondFactor is null || _userContexts is null
            ? SecondFactorLoginStep.None
            : await _secondFactorPolicy.GetLoginStepAsync(context);

        if (step == SecondFactorLoginStep.None)
            return await CompleteSignInAsync(context, secondFactor: false);

        _pending = new PendingLogin(context.User.Id, context.User.SecurityStamp, _time.GetUtcNow() + PendingLoginLifetime);
        Code = "";
        if (step == SecondFactorLoginStep.Verify)
        {
            Stage = LoginStage.SecondFactor;
        }
        else
        {
            Enrollment = await _secondFactor!.BeginEnrollmentAsync(context.User);
            _pending = _pending with { ExpiresAt = _time.GetUtcNow() + EnrollmentLifetime };
            Stage = LoginStage.Enrollment;
        }
        return false;
    }

    public async Task<bool> VerifySecondFactorAsync()
    {
        if (Stage != LoginStage.SecondFactor || !TryBeginStep()) return false;
        try
        {
            var context = await ResolvePendingAsync();
            if (context is null) return false;

            var result = await _secondFactor!.VerifyAsync(context.User, Code, _client.RemoteAddress);
            Code = "";
            if (result.Succeeded)
                return await CompleteSignInAsync(context, secondFactor: true);

            ShowCheckError(result);
            return false;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Second-factor check failed for the pending login");
            ErrorMessage = Resources.Web_Login_Failed;
            return false;
        }
        finally
        {
            IsLoading = false;
        }
    }

    /// <summary>Activates the factor set up during the login and shows the recovery codes.</summary>
    public async Task<bool> ConfirmEnrollmentAsync()
    {
        if (Stage != LoginStage.Enrollment || Enrollment is null || !TryBeginStep()) return false;
        try
        {
            var context = await ResolvePendingAsync();
            if (context is null) return false;

            var result = await _secondFactor!.ConfirmEnrollmentAsync(context.User, Enrollment.Secret, Code, _client.RemoteAddress);
            Code = "";
            if (result.Outcome != SecondFactorCheck.Success)
            {
                ShowCheckError(result);
                return false;
            }

            Enrollment = null;
            RecoveryCodes = result.RecoveryCodes;
            Stage = LoginStage.RecoveryCodes;
            return false;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Second-factor enrollment failed for the pending login");
            ErrorMessage = Resources.Web_Login_Failed;
            return false;
        }
        finally
        {
            IsLoading = false;
        }
    }

    /// <summary>Starts the session after the recovery codes were shown.</summary>
    public async Task<bool> FinishEnrollmentAsync()
    {
        if (Stage != LoginStage.RecoveryCodes || !TryBeginStep()) return false;
        try
        {
            var context = await ResolvePendingAsync();
            return context is not null && await CompleteSignInAsync(context, secondFactor: true);
        }
        finally
        {
            IsLoading = false;
        }
    }

    /// <summary>Back to the user name and password (drops the accepted password).</summary>
    public void CancelSecondFactor()
    {
        ResetSecondFactor();
        ErrorMessage = null;
    }

    private void ResetSecondFactor()
    {
        _pending = null;
        Code = "";
        Enrollment = null;
        RecoveryCodes = null;
        Stage = LoginStage.Credentials;
    }

    private bool TryBeginStep()
    {
        // A second submit (Enter twice, Enter + click) while one runs would check the same code
        // again, fail on the replay guard and raise a false "password is known" alarm.
        if (IsLoading) return false;
        ErrorMessage = null;
        if (_pending is null)
        {
            ResetSecondFactor();
            return false;
        }
        IsLoading = true;
        return true;
    }

    /// <summary>
    /// The pending account, re-read; <c>null</c> (and back to the credentials) when the
    /// pending sign-in expired, or the account was disabled or its credentials changed.
    /// </summary>
    private async Task<UserContext?> ResolvePendingAsync()
    {
        var pending = _pending!;
        if (_time.GetUtcNow() >= pending.ExpiresAt)
        {
            ResetSecondFactor();
            ErrorMessage = R("Web_Login_SecondFactor_Expired");
            return null;
        }

        var context = await _userContexts!.CreateByUserIdAsync(pending.UserId);
        if (context is not { User.IsEnabled: true } || context.User.SecurityStamp != pending.SecurityStamp)
        {
            ResetSecondFactor();
            ErrorMessage = Resources.Web_Login_InvalidCredentials;
            return null;
        }
        return context;
    }

    private void ShowCheckError(SecondFactorCheckResult result)
        => ErrorMessage = result.Outcome switch
        {
            SecondFactorCheck.LockedOut => string.Format(R("Web_Login_SecondFactor_LockedOut"),
                Math.Max(1, (int)Math.Ceiling(result.RetryAfter.TotalMinutes))),
            SecondFactorCheck.Unavailable => R("Web_Login_SecondFactor_Unavailable"),
            _ => R("Web_Login_SecondFactor_Invalid"),
        };

    private static string R(string key) => Resources.ResourceManager.GetString(key) ?? key;
}

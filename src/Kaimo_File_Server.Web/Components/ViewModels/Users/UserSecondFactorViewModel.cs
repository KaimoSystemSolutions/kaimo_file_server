using Kaimo_File_Server.Core.Domain.Identity;
using Kaimo_File_Server.Core.Language;
using Kaimo_File_Server.Core.Repositories.Identity;
using Kaimo_File_Server.Core.Security.Authentication;
using Kaimo_File_Server.Core.Security.Demo;
using Kaimo_File_Server.Core.Security.Management;
using Kaimo_File_Server.Core.Services.Management;
using Kaimo_File_Server.Web.Services;
using Kaimo_File_Server.Web.Services.SecondFactor;
using Microsoft.AspNetCore.Components.Authorization;

namespace Kaimo_File_Server.Web.Components.ViewModels;

/// <summary>What the two-factor section of the user panel is doing.</summary>
public enum SecondFactorPanelMode { None, Setup, Disable, Regenerate }

/// <summary>
/// User details → two-factor sign-in. For the own account: set up (after a password re-check,
/// throttled like a sign-in), remove (when the policy does not require it) and new recovery
/// codes (both after a valid code). For other accounts: the status, and the reset for an
/// administrator with <see cref="ManagementPermission.ResetSecondFactor"/> on the user. The own
/// factor is never reset through the admin action; permissions are re-checked on every click.
/// </summary>
public sealed class UserSecondFactorViewModel(
    AuthenticationStateProvider authState,
    IUserContextFactory userContextFactory,
    IManagementAuthService mgmtAuth,
    SecondFactorService secondFactor,
    ILoginService loginService,
    ClientConnectionInfo client,
    ILogger<UserSecondFactorViewModel> logger,
    JwtTokenService? jwt = null)
{
    private User? _user;

    public SecondFactorStatus? Status { get; private set; }
    public bool IsSelf { get; private set; }
    public bool CanReset { get; private set; }
    public SecondFactorPanelMode Mode { get; private set; }

    public bool CanSetUp => IsSelf && Status is { IsAvailable: true, IsEnrolled: false };
    public bool CanDisable => IsSelf && Status is { IsEnrolled: true, IsRequired: false };
    public bool CanRegenerate => IsSelf && Status is { IsEnrolled: true };

    /// <summary>Whether the section has anything to show for this user.</summary>
    public bool IsVisible => Status is { } s && (s.IsAvailable || s.IsEnrolled);

    public string Password { get; set; } = "";
    public string Code { get; set; } = "";
    public SecondFactorEnrollment? Enrollment { get; private set; }
    public IReadOnlyList<string>? RecoveryCodes { get; private set; }
    public bool ConfirmingReset { get; set; }
    public bool IsBusy { get; private set; }
    public string? ErrorMessage { get; private set; }

    public async Task LoadAsync(User user)
    {
        _user = user;
        Cancel();
        RecoveryCodes = null;
        try
        {
            var actor = await GetActorAsync();
            IsSelf = actor?.User.Id == user.Id;
            var target = IsSelf ? actor : await userContextFactory.CreateByUserIdAsync(user.Id);
            Status = target is null ? null : await secondFactor.GetStatusAsync(target);
            CanReset = await MayResetAsync(actor);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to load the second-factor state of user {UserId}", user.Id);
        }
    }

    public void StartSetup() { if (CanSetUp) Begin(SecondFactorPanelMode.Setup); }
    public void StartDisable() { if (CanDisable) Begin(SecondFactorPanelMode.Disable); }
    public void StartRegenerate() { if (CanRegenerate) Begin(SecondFactorPanelMode.Regenerate); }

    public void Cancel()
    {
        Mode = SecondFactorPanelMode.None;
        Password = "";
        Code = "";
        Enrollment = null;
        ConfirmingReset = false;
        ErrorMessage = null;
    }

    public void DismissRecoveryCodes() => RecoveryCodes = null;

    /// <summary>Checks the password, then shows the QR code.</summary>
    public async Task BeginSetupAsync()
    {
        if (Mode != SecondFactorPanelMode.Setup || _user is null) return;
        await RunAsync(async () =>
        {
            var password = Password;
            Password = "";
            var result = await loginService.AuthenticateAsync(_user.Username, password, client.RemoteAddress, client.Host);
            if (result.Outcome != LoginOutcome.Success || result.UserContext?.User.Id != _user.Id)
            {
                ErrorMessage = result.Outcome == LoginOutcome.LockedOut
                    ? string.Format(R("Web_User_CurrentPasswordLockedOut"), Math.Max(1, (int)Math.Ceiling(result.RetryAfter.TotalMinutes)))
                    : R("Web_User_CurrentPasswordWrong");
                return;
            }
            Enrollment = await secondFactor.BeginEnrollmentAsync(_user);
        });
    }

    /// <summary>Activates the factor with the first code and keeps the own session alive.</summary>
    public async Task ConfirmSetupAsync()
    {
        if (Mode != SecondFactorPanelMode.Setup || Enrollment is null || _user is null) return;
        await RunAsync(async () =>
        {
            var result = await secondFactor.ConfirmEnrollmentAsync(_user, Enrollment.Secret, Code, client.RemoteAddress);
            Code = "";
            if (result.Outcome != SecondFactorCheck.Success)
            {
                ErrorMessage = CheckError(result);
                return;
            }
            RecoveryCodes = result.RecoveryCodes;
            // From now on only sessions confirmed with a second factor are valid; renew this one.
            await RenewOwnSessionAsync();
            await ReloadStatusAsync();
            Mode = SecondFactorPanelMode.None;
            Enrollment = null;
        });
    }

    /// <summary>Removes the factor or creates new recovery codes, after a valid code.</summary>
    public async Task SubmitCodeAsync()
    {
        if (_user is null || Mode is not (SecondFactorPanelMode.Disable or SecondFactorPanelMode.Regenerate)) return;
        await RunAsync(async () =>
        {
            var self = await GetActorAsync();
            if (self?.User.Id != _user.Id) return;

            var result = Mode == SecondFactorPanelMode.Disable
                ? await secondFactor.DisableAsync(self, Code, client.RemoteAddress)
                : await secondFactor.RegenerateRecoveryCodesAsync(_user, Code, client.RemoteAddress);
            Code = "";
            if (result.Outcome != SecondFactorCheck.Success)
            {
                ErrorMessage = CheckError(result);
                return;
            }
            RecoveryCodes = result.RecoveryCodes;
            await ReloadStatusAsync();
            Mode = SecondFactorPanelMode.None;
        });
    }

    /// <summary>Administrator reset of another user's factor.</summary>
    public async Task ResetAsync()
    {
        if (_user is null) return;
        await RunAsync(async () =>
        {
            var actor = await GetActorAsync();
            if (!await MayResetAsync(actor))
            {
                CanReset = false;
                ErrorMessage = R("Web_Settings_NoPermissionChange");
                return;
            }
            if (!await secondFactor.ResetAsync(_user, actor!.User.Id))
                ErrorMessage = R("Web_SecondFactor_ResetNothingToReset"); // e.g. another administrator was faster
            ConfirmingReset = false;
            await ReloadStatusAsync();
            CanReset = false;
        });
    }

    private void Begin(SecondFactorPanelMode mode)
    {
        Cancel();
        RecoveryCodes = null;
        Mode = mode;
    }

    private async Task RunAsync(Func<Task> action)
    {
        ErrorMessage = null;
        IsBusy = true;
        try
        {
            await action();
        }
        catch (ReadOnlyDemoException)
        {
            ErrorMessage = R("Web_Demo_ReadOnlyNotice");
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Second-factor action failed for user {UserId}", _user?.Id);
            ErrorMessage = R("Web_Error_SaveFailed");
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task ReloadStatusAsync()
    {
        var target = await userContextFactory.CreateByUserIdAsync(_user!.Id);
        if (target is not null)
            Status = await secondFactor.GetStatusAsync(target);
    }

    private async Task<bool> MayResetAsync(UserContext? actor)
        => actor is not null && _user is not null && actor.User.Id != _user.Id
           && Status is { IsEnrolled: true }
           && await mgmtAuth.CanManageUserAsync(actor, _user.Id, ManagementPermission.ResetSecondFactor);

    private async Task RenewOwnSessionAsync()
    {
        if (jwt is null || authState is not JwtAuthenticationStateProvider provider) return;
        var context = await userContextFactory.CreateByUserIdAsync(_user!.Id);
        if (context is null) return;

        var token = jwt.GenerateToken(context.User.Id, context.User.Username, context.User.Name,
            context.Roles.Select(r => r.Name), securityStamp: context.User.SecurityStamp,
            authMethods: JwtTokenService.AuthMethods(secondFactor: true));
        if (!await provider.StoreSessionTokenAsync(token))
            logger.LogWarning("Could not renew the session cookie after setting up the second factor; " +
                              "this session ends at the next revalidation");
    }

    private async Task<UserContext?> GetActorAsync()
    {
        var state = await authState.GetAuthenticationStateAsync();
        var username = state.User.Identity?.Name;
        return string.IsNullOrEmpty(username) ? null : await userContextFactory.CreateByUsernameAsync(username);
    }

    private static string CheckError(SecondFactorCheckResult result) => result.Outcome switch
    {
        SecondFactorCheck.LockedOut => string.Format(R("Web_Login_SecondFactor_LockedOut"),
            Math.Max(1, (int)Math.Ceiling(result.RetryAfter.TotalMinutes))),
        SecondFactorCheck.Unavailable => R("Web_Login_SecondFactor_Unavailable"),
        _ => R("Web_Login_SecondFactor_Invalid"),
    };

    private static string R(string key) => Resources.ResourceManager.GetString(key) ?? key;
}

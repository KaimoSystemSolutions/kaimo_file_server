using Kaimo_File_Server.Core.Domain.Identity;
using Kaimo_File_Server.Core.Language;
using Kaimo_File_Server.Core.Repositories;
using Kaimo_File_Server.Core.Security;
using Kaimo_File_Server.Core.Services;
using Kaimo_File_Server.Infrastructure.Notifications;
using Kaimo_File_Server.Web.Services.Notifications;
using Microsoft.AspNetCore.Components.Authorization;

namespace Kaimo_File_Server.Web.Components.ViewModels;

/// <summary>
/// Backs the "Mail server" settings tab: the SMTP gateway configuration and the test mail.
/// Gated by <see cref="ManagementPermission.ManageMailServer"/>, which is re-checked on every
/// mutation so a stale page cannot change the settings after the permission was revoked.
/// The password is write-only: it is never loaded into the page, only replaced.
/// </summary>
public sealed class MailServerSettingsViewModel(
    AuthenticationStateProvider authState,
    IUserContextFactory userContextFactory,
    IManagementAuthService mgmtAuth,
    ISmtpConfigStore store,
    ISmtpMailSender sender,
    ICredentialVault vault,
    NotificationMailComposer composer,
    ILogger<MailServerSettingsViewModel> logger)
{
    private UserContext? _actor;

    public bool IsLoading { get; private set; } = true;
    public bool CanManage { get; private set; }
    public bool IsSaving { get; private set; }
    public bool IsTesting { get; private set; }

    /// <summary>The edited settings; <see cref="SmtpSettings.EncryptedPassword"/> is kept as loaded.</summary>
    public SmtpSettings Settings { get; private set; } = SmtpSettings.Default();

    /// <summary>A new password to store; empty keeps the current one.</summary>
    public string NewPassword { get; set; } = string.Empty;

    public bool HasStoredPassword => !string.IsNullOrEmpty(Settings.EncryptedPassword);
    public SmtpStatus Status { get; private set; } = new();

    /// <summary>Recipient of the test mail; defaults to the current user's address.</summary>
    public string TestAddress { get; set; } = string.Empty;
    public SmtpTestResult? TestResult { get; private set; }

    public string? SuccessMessage { get; private set; }
    public string? ErrorMessage { get; private set; }

    public async Task LoadAsync()
    {
        IsLoading = true;
        try
        {
            _actor = await GetActorAsync();
            CanManage = await HasPermissionAsync();
            if (!CanManage) return;

            Settings = await store.GetAsync();
            Status = await store.GetStatusAsync();
            TestAddress = _actor?.User.Email ?? string.Empty;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to load the mail server settings");
            ErrorMessage = R("Web_Settings_LoadFailed");
        }
        finally
        {
            IsLoading = false;
        }
    }

    public async Task SaveAsync()
    {
        ClearMessages();
        if (!await HasPermissionAsync()) { ErrorMessage = R("Web_Settings_NoPermissionChange"); return; }

        var error = Validate();
        if (error is not null) { ErrorMessage = error; return; }

        IsSaving = true;
        try
        {
            var toSave = Settings with { };
            if (toSave.AuthMode == SmtpAuthMode.None)
                toSave.EncryptedPassword = null;
            else if (!string.IsNullOrEmpty(NewPassword))
                toSave.EncryptedPassword = vault.Protect(NewPassword, SmtpSettings.PasswordContext);
            else if (await store.GetAsync() is { EncryptedPassword: { Length: > 0 } } stored
                     && !toSave.CanReuseStoredPasswordOf(stored))
            {
                // The stored password must never follow a changed server/user to a new target.
                ErrorMessage = R("Web_MailServer_Error_PasswordRequired");
                return;
            }

            await store.SetAsync(toSave);
            Settings = await store.GetAsync();
            NewPassword = string.Empty;
            SuccessMessage = R("Web_ChangesSaved");
        }
        catch (ReadOnlyDemoException)
        {
            ErrorMessage = R("Web_Demo_ReadOnlyNotice");
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to save the mail server settings");
            ErrorMessage = R("Web_Error_SaveFailed");
        }
        finally
        {
            IsSaving = false;
        }
    }

    /// <summary>Sends a test mail with the settings as currently entered (saved or not).</summary>
    public async Task SendTestAsync()
    {
        ClearMessages();
        TestResult = null;
        if (!await HasPermissionAsync()) { ErrorMessage = R("Web_Settings_NoPermissionChange"); return; }
        if (!NotificationRecipientResolver.TryNormalizeAddress(TestAddress, out var to))
        {
            ErrorMessage = R("Web_MailServer_TestAddressInvalid");
            return;
        }

        IsTesting = true;
        try
        {
            var environment = await composer.LoadEnvironmentAsync();
            var language = environment.DefaultLanguage;
            var mail = await composer.RenderAsync(
                DefaultMailTemplates.TestMail(language), environment with { ServerName = NameOr(Settings.FromName) },
                new Dictionary<string, string>(), language,
                _actor?.User.Name ?? to, to, DateTime.UtcNow);
            TestResult = await sender.TestAsync(
                Settings, Settings.AuthMode == SmtpAuthMode.Password ? NewPassword : null,
                NotificationMailComposer.ToMessage(environment, to, mail.Subject, mail.Html, mail.Text));

            try
            {
                await store.RecordStatusAsync(TestResult.Success ? null : TestResult.Error);
                Status = await store.GetStatusAsync();
            }
            catch (ReadOnlyDemoException) { /* status is informational only */ }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Sending the SMTP test mail failed");
            TestResult = new SmtpTestResult(false, ex.Message);
        }
        finally
        {
            IsTesting = false;
        }
    }

    public void ClearMessages()
    {
        SuccessMessage = null;
        ErrorMessage = null;
    }

    private string? Validate()
    {
        if (!Settings.Enabled) return null;
        if (string.IsNullOrWhiteSpace(Settings.Host)) return R("Web_MailServer_HostRequired");
        if (!NotificationRecipientResolver.TryNormalizeAddress(Settings.FromAddress, out _))
            return R("Web_MailServer_FromInvalid");
        if (!string.IsNullOrWhiteSpace(Settings.ReplyTo)
            && !NotificationRecipientResolver.TryNormalizeAddress(Settings.ReplyTo, out _))
            return R("Web_MailServer_ReplyToInvalid");
        return null;
    }

    private async Task<bool> HasPermissionAsync()
        => _actor is not null
           && (await mgmtAuth.GetEffectivePermissionsAtAsync(_actor, ScopeType.Global, Guid.Empty))
               .HasFlag(ManagementPermission.ManageMailServer);

    private async Task<UserContext?> GetActorAsync()
    {
        var state = await authState.GetAuthenticationStateAsync();
        var username = state.User.Identity?.Name;
        return string.IsNullOrEmpty(username) ? null : await userContextFactory.CreateByUsernameAsync(username);
    }

    private static string NameOr(string name) => string.IsNullOrWhiteSpace(name) ? "Kaimo Files" : name;

    private static string R(string key) => Resources.ResourceManager.GetString(key) ?? key;
}

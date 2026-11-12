using Kaimo_File_Server.Core.Domain.Identity;
using Kaimo_File_Server.Core.Language;
using Kaimo_File_Server.Core.Repositories.Identity;
using Kaimo_File_Server.Core.Security.Authentication;
using Kaimo_File_Server.Core.Security.Demo;
using Kaimo_File_Server.Core.Security.Management;
using Kaimo_File_Server.Core.Services.Management;
using Kaimo_File_Server.Infrastructure.Configuration;
using Kaimo_File_Server.Web.Services;
using Microsoft.AspNetCore.Components.Authorization;

namespace Kaimo_File_Server.Web.Components.ViewModels;

/// <summary>
/// Backs the "Login protection" settings tab: the brute-force thresholds of the credential
/// login (web UI, client API, WebDAV), of share-link passwords and of the SMB account lockout,
/// all stored as <see cref="LoginProtectionSettings"/> keys. Out-of-range input is reported per
/// field instead of being clamped silently; a reported field is cleared as soon as it is corrected.
/// The SMB values are not checked while the SMB lockout is off. Gated by
/// <see cref="ManagementPermission.ManageSystemSettings"/>, re-checked on save.
/// </summary>
public sealed class LoginProtectionSettingsViewModel(
    AuthenticationStateProvider authState,
    IUserContextFactory userContextFactory,
    IManagementAuthService mgmtAuth,
    IConfigRepository config,
    ILogger<LoginProtectionSettingsViewModel> logger,
    ToastService? toasts = null)
{
    private UserContext? _actor;
    private Dictionary<string, string> _fieldErrors = new();

    public bool IsLoading { get; private set; } = true;
    public bool CanManage { get; private set; }
    public bool IsSaving { get; private set; }
    // Also raised as toasts: the tab's banners scroll out of view on long settings pages.
    public string? SuccessMessage { get; private set { field = value; if (value is not null) toasts?.Show(value, ToastType.Success); } }
    public string? ErrorMessage { get; private set { field = value; if (value is not null) toasts?.Show(value, ToastType.Error); } }

    // ── Web UI, client API, WebDAV ──

    public int MaxAttempts { get; set { field = value; Revalidate(nameof(MaxAttempts), value); } } = LoginProtectionSettings.DefaultMaxAttempts;
    public int LockoutMinutes { get; set { field = value; Revalidate(nameof(LockoutMinutes), value); } } = LoginProtectionSettings.DefaultLockoutMinutes;
    public int AccountWideFactor { get; set { field = value; Revalidate(nameof(AccountWideFactor), value); } } = LoginProtectionSettings.DefaultAccountWideFactor;
    public int IpMaxAttempts { get; set { field = value; Revalidate(nameof(IpMaxAttempts), value); } } = LoginProtectionSettings.DefaultIpMaxAttempts;
    public int IpLockoutMinutes { get; set { field = value; Revalidate(nameof(IpLockoutMinutes), value); } } = LoginProtectionSettings.DefaultIpLockoutMinutes;

    // ── Share-link passwords ──

    public int ShareLinkMaxAttemptsPerAddress { get; set { field = value; Revalidate(nameof(ShareLinkMaxAttemptsPerAddress), value); } } = LoginProtectionSettings.DefaultShareLinkMaxAttemptsPerAddress;
    public int ShareLinkMaxAttemptsPerLink { get; set { field = value; Revalidate(nameof(ShareLinkMaxAttemptsPerLink), value); } } = LoginProtectionSettings.DefaultShareLinkMaxAttemptsPerLink;
    public int ShareLinkLockoutMinutes { get; set { field = value; Revalidate(nameof(ShareLinkLockoutMinutes), value); } } = LoginProtectionSettings.DefaultShareLinkLockoutMinutes;

    // ── SMB ──

    public bool SmbLockoutEnabled
    {
        get;
        set
        {
            field = value;
            if (value) return;
            foreach (var smbField in SmbFields) _fieldErrors.Remove(smbField);
            ClearInvalidBannerWhenResolved();
        }
    } = true;

    public int SmbLockoutAttempts { get; set { field = value; Revalidate(nameof(SmbLockoutAttempts), value); } } = LoginProtectionSettings.DefaultSmbLockoutAttempts;
    public int SmbLockoutMinutes { get; set { field = value; Revalidate(nameof(SmbLockoutMinutes), value); } } = LoginProtectionSettings.DefaultSmbLockoutMinutes;

    /// <summary>Validation messages of the last save, keyed by property name.</summary>
    public IReadOnlyDictionary<string, string> FieldErrors => _fieldErrors;

    public string? ErrorFor(string field) => _fieldErrors.GetValueOrDefault(field);

    /// <summary>
    /// The account-wide ceiling the current values result in (shown next to the factor), or
    /// <c>null</c> while either input is out of range and the product would be meaningless.
    /// </summary>
    public long? AccountWideAttempts
        => IsInRange(nameof(MaxAttempts), MaxAttempts) && IsInRange(nameof(AccountWideFactor), AccountWideFactor)
            ? (long)MaxAttempts * AccountWideFactor
            : null;

    public async Task LoadAsync()
    {
        IsLoading = true;
        try
        {
            _actor = await GetActorAsync();
            CanManage = await HasPermissionAsync();
            if (!CanManage) return;

            Apply(await config.LoadLoginProtectionAsync());
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to load the login protection settings");
            ErrorMessage = R("Web_Settings_LoadFailed");
        }
        finally
        {
            IsLoading = false;
        }
    }

    public void ClearMessages()
    {
        SuccessMessage = null;
        ErrorMessage = null;
    }

    /// <summary>Validates every field and, when all are in range, writes all keys in one batch.</summary>
    public async Task SaveAsync()
    {
        ClearMessages();
        if (!await HasPermissionAsync()) { ErrorMessage = R("Web_Settings_NoPermissionChange"); return; }

        _fieldErrors = Validate();
        if (_fieldErrors.Count > 0)
        {
            ErrorMessage = R("Web_LoginProtection_Error_Invalid");
            return;
        }

        IsSaving = true;
        try
        {
            await config.SetManyAsync(BuildValues());
            logger.LogInformation(
                "Login protection saved: {MaxAttempts}/{LockoutMinutes} min per user and address, factor {Factor}, " +
                "{IpMaxAttempts}/{IpLockoutMinutes} min per address, share links {LinkPerAddress}/{LinkPerLink}/{LinkMinutes} min, " +
                "SMB lockout {SmbEnabled} {SmbAttempts}/{SmbMinutes} min",
                MaxAttempts, LockoutMinutes, AccountWideFactor, IpMaxAttempts, IpLockoutMinutes,
                ShareLinkMaxAttemptsPerAddress, ShareLinkMaxAttemptsPerLink, ShareLinkLockoutMinutes,
                SmbLockoutEnabled, SmbLockoutAttempts, SmbLockoutMinutes);
            SuccessMessage = R("Web_ChangesSaved");
        }
        catch (ReadOnlyDemoException)
        {
            ErrorMessage = R("Web_Demo_ReadOnlyNotice");
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to save the login protection settings");
            ErrorMessage = R("Web_Error_SaveFailed");
        }
        finally
        {
            IsSaving = false;
        }
    }

    private void Apply(LoginProtectionSettings settings)
    {
        MaxAttempts = settings.MaxAttempts;
        LockoutMinutes = settings.LockoutMinutes;
        AccountWideFactor = settings.AccountWideFactor;
        IpMaxAttempts = settings.IpMaxAttempts;
        IpLockoutMinutes = settings.IpLockoutMinutes;
        ShareLinkMaxAttemptsPerAddress = settings.ShareLinkMaxAttemptsPerAddress;
        ShareLinkMaxAttemptsPerLink = settings.ShareLinkMaxAttemptsPerLink;
        ShareLinkLockoutMinutes = settings.ShareLinkLockoutMinutes;
        SmbLockoutEnabled = settings.SmbLockoutEnabled;
        SmbLockoutAttempts = settings.SmbLockoutAttempts;
        SmbLockoutMinutes = settings.SmbLockoutMinutes;
        _fieldErrors = new();
    }

    // Upper bound per numeric field, matching what LoginProtectionSettings.Normalize enforces; the lower bound is 1.
    private static readonly Dictionary<string, int> Bounds = new()
    {
        [nameof(MaxAttempts)] = LoginProtectionSettings.MaxAttemptsLimit,
        [nameof(LockoutMinutes)] = LoginProtectionSettings.MaxLockoutMinutes,
        [nameof(AccountWideFactor)] = LoginProtectionSettings.MaxAccountWideFactor,
        [nameof(IpMaxAttempts)] = LoginProtectionSettings.MaxAttemptsLimit,
        [nameof(IpLockoutMinutes)] = LoginProtectionSettings.MaxLockoutMinutes,
        [nameof(ShareLinkMaxAttemptsPerAddress)] = LoginProtectionSettings.MaxAttemptsLimit,
        [nameof(ShareLinkMaxAttemptsPerLink)] = LoginProtectionSettings.MaxAttemptsLimit,
        [nameof(ShareLinkLockoutMinutes)] = LoginProtectionSettings.MaxLockoutMinutes,
        [nameof(SmbLockoutAttempts)] = LoginProtectionSettings.MaxAttemptsLimit,
        [nameof(SmbLockoutMinutes)] = LoginProtectionSettings.MaxLockoutMinutes,
    };

    private static readonly string[] SmbFields = [nameof(SmbLockoutAttempts), nameof(SmbLockoutMinutes)];

    private static bool IsInRange(string field, int value) => value >= 1 && value <= Bounds[field];

    /// <summary>Every numeric field must lie in range; the SMB fields only while the SMB lockout is on.</summary>
    private Dictionary<string, string> Validate()
    {
        (string Field, int Value)[] fields =
        [
            (nameof(MaxAttempts), MaxAttempts),
            (nameof(LockoutMinutes), LockoutMinutes),
            (nameof(AccountWideFactor), AccountWideFactor),
            (nameof(IpMaxAttempts), IpMaxAttempts),
            (nameof(IpLockoutMinutes), IpLockoutMinutes),
            (nameof(ShareLinkMaxAttemptsPerAddress), ShareLinkMaxAttemptsPerAddress),
            (nameof(ShareLinkMaxAttemptsPerLink), ShareLinkMaxAttemptsPerLink),
            (nameof(ShareLinkLockoutMinutes), ShareLinkLockoutMinutes),
            (nameof(SmbLockoutAttempts), SmbLockoutAttempts),
            (nameof(SmbLockoutMinutes), SmbLockoutMinutes),
        ];

        return fields
            .Where(f => !IsInRange(f.Field, f.Value) && (SmbLockoutEnabled || !SmbFields.Contains(f.Field)))
            .ToDictionary(f => f.Field, f => string.Format(R("Web_LoginProtection_Error_Range"), 1, Bounds[f.Field]));
    }

    /// <summary>
    /// All keys of the tab. While the SMB lockout is off its (unchecked) numbers are only written
    /// when in range, so an invalid entry in a disabled field keeps the stored value.
    /// </summary>
    private Dictionary<string, object> BuildValues()
    {
        var values = new Dictionary<string, object>
        {
            [LoginProtectionSettings.MaxAttemptsKey] = MaxAttempts,
            [LoginProtectionSettings.LockoutMinutesKey] = LockoutMinutes,
            [LoginProtectionSettings.AccountWideFactorKey] = AccountWideFactor,
            [LoginProtectionSettings.IpMaxAttemptsKey] = IpMaxAttempts,
            [LoginProtectionSettings.IpLockoutMinutesKey] = IpLockoutMinutes,
            [LoginProtectionSettings.ShareLinkMaxAttemptsPerAddressKey] = ShareLinkMaxAttemptsPerAddress,
            [LoginProtectionSettings.ShareLinkMaxAttemptsPerLinkKey] = ShareLinkMaxAttemptsPerLink,
            [LoginProtectionSettings.ShareLinkLockoutMinutesKey] = ShareLinkLockoutMinutes,
            [LoginProtectionSettings.SmbLockoutEnabledKey] = SmbLockoutEnabled,
            [LoginProtectionSettings.SmbLockoutAttemptsKey] = SmbLockoutAttempts,
            [LoginProtectionSettings.SmbLockoutMinutesKey] = SmbLockoutMinutes,
        };
        if (!IsInRange(nameof(SmbLockoutAttempts), SmbLockoutAttempts)) values.Remove(LoginProtectionSettings.SmbLockoutAttemptsKey);
        if (!IsInRange(nameof(SmbLockoutMinutes), SmbLockoutMinutes)) values.Remove(LoginProtectionSettings.SmbLockoutMinutesKey);
        return values;
    }

    // Drops the error of a field once it is back in range; new errors appear only on save.
    private void Revalidate(string field, int value)
    {
        if (!_fieldErrors.ContainsKey(field) || !IsInRange(field, value)) return;
        _fieldErrors.Remove(field);
        ClearInvalidBannerWhenResolved();
    }

    private void ClearInvalidBannerWhenResolved()
    {
        if (_fieldErrors.Count == 0 && ErrorMessage == R("Web_LoginProtection_Error_Invalid")) ErrorMessage = null;
    }

    private async Task<bool> HasPermissionAsync()
        => _actor is not null && await mgmtAuth.HasGlobalPermissionAsync(_actor, ManagementPermission.ManageSystemSettings);

    private async Task<UserContext?> GetActorAsync()
    {
        var state = await authState.GetAuthenticationStateAsync();
        var username = state.User.Identity?.Name;
        return string.IsNullOrEmpty(username) ? null : await userContextFactory.CreateByUsernameAsync(username);
    }

    private static string R(string key) => Resources.ResourceManager.GetString(key) ?? key;
}

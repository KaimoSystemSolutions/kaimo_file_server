using Kaimo_File_Server.Core.Domain.Identity;
using Kaimo_File_Server.Core.Repositories;
using Kaimo_File_Server.Core.Security;
using Kaimo_File_Server.Core.Services;
using Kaimo_File_Server.Core.Services.DataServices;
using Kaimo_File_Server.Infrastructure.Configuration;
using Microsoft.AspNetCore.Components.Authorization;
using Kaimo_File_Server.Core.Language;
using Kaimo_File_Server.Web.DynamicHelpers;

namespace Kaimo_File_Server.Web.Components.ViewModels;

public class SettingsViewModel
{
    private readonly IConfigRepository _config;
    private readonly IManagementAuthService _mgmtAuth;
    private readonly IUserContextFactory _userContextFactory;
    private readonly AuthenticationStateProvider _authState;
    private readonly ISystemInfoService _sysInfo;
    private readonly ILogger<SettingsViewModel> _logger;

    public SettingsViewModel(
        IConfigRepository config,
        IManagementAuthService mgmtAuth,
        IUserContextFactory userContextFactory,
        AuthenticationStateProvider authState,
        ISystemInfoService sysInfo,
        ILogger<SettingsViewModel> logger)
    {
        _config = config;
        _mgmtAuth = mgmtAuth;
        _userContextFactory = userContextFactory;
        _authState = authState;
        _sysInfo = sysInfo;
        _logger = logger;
    }

    // ── State ──

    public bool IsLoading { get; private set; }
    public string? ErrorMessage { get; private set; }
    public string? SuccessMessage { get; private set; }

    // ── Permissions ──

    /// <summary>May edit global settings such as the application language.</summary>
    public bool CanManageSettings { get; private set; }

    /// <summary>May start/stop data services (SMB, …).</summary>
    public bool CanManageDataServices { get; private set; }

    /// <summary>True if the user may access the settings page at all.</summary>
    public bool CanAccessPage => CanManageSettings || CanManageDataServices;

    // ── Language ──

    public string SelectedLanguage { get; set; } = "de";

    public static readonly Dictionary<string, string> AvailableLanguages = new()
    {
        ["de"] = "Deutsch",
        ["en"] = "English",
    };

    // ── Data Services (SMB) ──

    /// <summary>Desired state of the SMB service (config flag).</summary>
    public bool SmbEnabled { get; set; } = true;

    /// <summary>Last status the SMB host reported back, or "Unbekannt".</summary>
    public string SmbStatus { get; private set; } = "Unbekannt";

    // ── Password Policy ──

    /// <summary>Globally enforced password requirements (working copy).</summary>
    public PasswordPolicy PwPolicy { get; private set; } = PasswordPolicy.Default();

    // ── System Info (IP / Storage / RAM) ──

    public string HostName { get; private set; } = "";
    public IReadOnlyList<NetworkAddressInfo> NetworkAddresses { get; private set; } = [];
    public StorageUsageInfo? StorageUsage { get; private set; }
    public MemoryUsageInfo? MemoryUsage { get; private set; }

    /// <summary>The server's public IP, once resolved. Null = not (yet) resolved.</summary>
    public string? PublicIp { get; private set; }

    /// <summary>True while the public IP is being fetched from the echo service.</summary>
    public bool PublicIpLoading { get; private set; }

    // ── Load ──

    public async Task LoadAsync()
    {
        try
        {
            IsLoading = true;
            ErrorMessage = null;

            await LoadPermissionsAsync();

            if (CanManageSettings)
            {
                SelectedLanguage = await _config.GetStringAsync("app.language", "de");
                CtxConfig = await _config.GetAsync(
                    ContextMenuConfig.ConfigKey, ContextMenuConfig.Default());
                PwPolicy = await _config.GetAsync(
                    PasswordPolicy.ConfigKey, PasswordPolicy.Default());
                RefreshSystemInfo();
            }

            if (CanManageDataServices)
            {
                SmbEnabled = await _config.GetBoolAsync(
                    DataServiceKeys.EnabledKey("smb"), fallback: true);
                // Status is written by the host process → read fresh, not cached.
                SmbStatus = await _config.GetFreshAsync(
                    DataServiceKeys.StatusKey("smb"), "Unbekannt");
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load settings");
            ErrorMessage = Resources.Web_Settings_LoadFailed;
        }
        finally
        {
            IsLoading = false;
        }
    }

    private async Task LoadPermissionsAsync()
    {
        var actor = await BuildActorContextAsync();
        if (actor is null)
        {
            CanManageSettings = false;
            CanManageDataServices = false;
            return;
        }

        // Global settings are unrestricted/global by nature → require Global scope.
        CanManageSettings = await _mgmtAuth.HasGlobalPermissionAsync(
            actor, ManagementPermission.ManageSystemSettings);
        CanManageDataServices = await _mgmtAuth.HasGlobalPermissionAsync(
            actor, ManagementPermission.ManageDataServices);
    }

    private async Task<UserContext?> BuildActorContextAsync()
    {
        var state = await _authState.GetAuthenticationStateAsync();
        var username = state.User.Identity?.Name;
        if (string.IsNullOrEmpty(username)) return null;
        return await _userContextFactory.CreateByUsernameAsync(username);
    }

    // ── Save Language ──

    public async Task<bool> SaveLanguageAsync()
    {
        ErrorMessage = null;
        SuccessMessage = null;

        if (!CanManageSettings)
        {
            ErrorMessage = Resources.Web_Settings_NoPermissionChange;
            return false;
        }

        try
        {
            await _config.SetAsync("app.language", SelectedLanguage);

            _logger.LogInformation("Language changed to '{Lang}'", SelectedLanguage);
            SuccessMessage = Resources.Web_Settings_LanguageSaved;
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to save language setting");
            ErrorMessage = Resources.Web_Settings_LanguageSaveFailed;
            return false;
        }
    }

    // ── Save Data Services ──

    public async Task<bool> SaveDataServicesAsync()
    {
        ErrorMessage = null;
        SuccessMessage = null;

        if (!CanManageDataServices)
        {
            ErrorMessage = Resources.Web_Settings_NoPermissionDataServices;
            return false;
        }

        try
        {
            await _config.SetAsync(DataServiceKeys.EnabledKey("smb"), SmbEnabled);

            _logger.LogInformation("SMB service desired state set to {Enabled}", SmbEnabled);
            SuccessMessage = SmbEnabled
                ? Resources.Web_Settings_SmbEnabled
                : Resources.Web_Settings_SmbDisabled;
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to save data service setting");
            ErrorMessage = Resources.Web_Settings_DataServiceSaveFailed;
            return false;
        }
    }

    /// <summary>Re-reads the reported status of the SMB service from the config store.</summary>
    public async Task RefreshDataServiceStatusAsync()
    {
        if (!CanManageDataServices) return;
        SmbStatus = await _config.GetFreshAsync(DataServiceKeys.StatusKey("smb"), "Unbekannt");
    }

    // ── Save Password Policy ──

    public async Task<bool> SavePasswordPolicyAsync()
    {
        ErrorMessage = null;
        SuccessMessage = null;

        if (!CanManageSettings)
        {
            ErrorMessage = Resources.Web_Settings_NoPermissionChange;
            return false;
        }

        // Guard against a nonsensical minimum that would lock everyone out of
        // creating passwords; clamp to a sane range.
        PwPolicy.MinLength = Math.Clamp(PwPolicy.MinLength, 1, 128);

        try
        {
            await _config.SetAsync(PasswordPolicy.ConfigKey, PwPolicy);
            _logger.LogInformation("Password policy saved (MinLength={Min})", PwPolicy.MinLength);
            SuccessMessage = Resources.Web_Settings_PwPolicySaved;
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to save password policy");
            ErrorMessage = Resources.Web_Settings_PwPolicySaveFailed;
            return false;
        }
    }

    // ── System Info (IP / Storage / RAM) ──

    /// <summary>Re-samples host network, storage and memory information (local, instant).</summary>
    public void RefreshSystemInfo()
    {
        if (!CanManageSettings) return;
        HostName = _sysInfo.HostName;
        NetworkAddresses = _sysInfo.GetNetworkAddresses();
        StorageUsage = _sysInfo.GetStorageUsage();
        MemoryUsage = _sysInfo.GetMemoryUsage();
    }

    /// <summary>
    /// Resolves the server's public IP via an outbound call. No-op if already
    /// resolved unless <paramref name="force"/> is set. Sets <see cref="PublicIpLoading"/>
    /// across the await so the UI can show a spinner.
    /// </summary>
    public async Task LoadPublicIpAsync(bool force = false)
    {
        if (!CanManageSettings) return;
        if (PublicIp is not null && !force) return;

        PublicIpLoading = true;
        PublicIp = null;
        try
        {
            PublicIp = await _sysInfo.GetPublicIpAsync();
        }
        finally
        {
            PublicIpLoading = false;
        }
    }

    /// <summary>Formats a byte count as a human-readable size (e.g. "1.4 GB").</summary>
    public string FormatBytes(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB", "PB"];
        double size = bytes;
        int unit = 0;
        while (size >= 1024 && unit < units.Length - 1)
        {
            size /= 1024;
            unit++;
        }
        return $"{size:0.#} {units[unit]}";
    }

    // ── Context Menu (per-scope layout) ──

    /// <summary>The globally configured, per-scope context-menu layout (working copy).</summary>
    public ContextMenuConfig CtxConfig { get; private set; } = ContextMenuConfig.Default();

    /// <summary>The category currently being edited in the GUI.</summary>
    public ContextMenuScope SelectedScope { get; private set; } = ContextMenuScope.Folder;

    /// <summary>All editable categories.</summary>
    public static IReadOnlyList<ContextMenuScope> ContextScopes { get; } = Enum.GetValues<ContextMenuScope>();

    /// <summary>Commands assigned to the selected scope, in order.</summary>
    public IReadOnlyList<ContextCommand> AssignedCommands =>
        WorkingList()
            .Select(ContextCommandCatalog.ById)
            .Where(c => c is not null)
            .Select(c => c!)
            .ToList();

    /// <summary>Valid commands for the selected scope that are not yet assigned.</summary>
    public IReadOnlyList<ContextCommand> AvailableCommands
    {
        get
        {
            var assigned = WorkingList();
            return ContextCommandCatalog.ForScope(SelectedScope)
                .Where(c => !assigned.Contains(c.Id))
                .ToList();
        }
    }

    /// <summary>
    /// All valid commands for the selected scope: assigned ones first (in their
    /// configured order), then the remaining unassigned ones. Drives the single
    /// checkbox list in the builder GUI.
    /// </summary>
    public IReadOnlyList<ContextCommand> ScopeCommandsOrdered
    {
        get
        {
            var assigned = AssignedCommands;
            var assignedIds = assigned.Select(c => c.Id).ToHashSet();
            var rest = ContextCommandCatalog.ForScope(SelectedScope)
                .Where(c => !assignedIds.Contains(c.Id));
            return assigned.Concat(rest).ToList();
        }
    }

    /// <summary>True if the command is currently part of the selected scope's menu.</summary>
    public bool IsAssigned(string id) => WorkingList().Contains(id);

    public void SelectScope(ContextMenuScope scope) => SelectedScope = scope;

    /// <summary>Adds the command if absent, removes it if present.</summary>
    public void ToggleCommand(string id)
    {
        var list = WorkingList();
        if (list.Contains(id)) list.Remove(id);
        else list.Add(id);
    }

    public void AddCommand(string id)
    {
        var list = WorkingList();
        if (!list.Contains(id)) list.Add(id);
    }

    public void RemoveCommand(string id) => WorkingList().Remove(id);

    public void MoveUp(string id)
    {
        var list = WorkingList();
        var i = list.IndexOf(id);
        if (i > 0) (list[i - 1], list[i]) = (list[i], list[i - 1]);
    }

    public void MoveDown(string id)
    {
        var list = WorkingList();
        var i = list.IndexOf(id);
        if (i >= 0 && i < list.Count - 1) (list[i + 1], list[i]) = (list[i], list[i + 1]);
    }

    public void ResetScopeToDefault()
        => CtxConfig.SetScope(SelectedScope, ContextMenuConfig.DefaultForScope(SelectedScope));

    public async Task<bool> SaveContextMenuAsync()
    {
        ErrorMessage = null;
        SuccessMessage = null;

        if (!CanManageSettings)
        {
            ErrorMessage = Resources.Web_Settings_NoPermissionChange;
            return false;
        }

        try
        {
            await _config.SetAsync(ContextMenuConfig.ConfigKey, CtxConfig);
            _logger.LogInformation("Context menu layout saved");
            SuccessMessage = Resources.Web_Settings_CtxSaved;
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to save context menu layout");
            ErrorMessage = Resources.Web_Settings_CtxSaveFailed;
            return false;
        }
    }

    /// <summary>Mutable, guaranteed-present command list for the selected scope.</summary>
    private List<string> WorkingList()
    {
        var key = SelectedScope.ToString();
        if (!CtxConfig.Menus.TryGetValue(key, out var list) || list is null)
        {
            list = ContextMenuConfig.DefaultForScope(SelectedScope);
            CtxConfig.Menus[key] = list;
        }
        return list;
    }

    /// <summary>Clears transient messages (call on tab switch).</summary>
    public void ClearMessages()
    {
        ErrorMessage = null;
        SuccessMessage = null;
    }
}

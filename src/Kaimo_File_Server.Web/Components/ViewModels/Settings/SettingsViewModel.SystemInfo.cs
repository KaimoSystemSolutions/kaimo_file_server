using Kaimo_File_Server.Core.Language;
using Kaimo_File_Server.Core.Services;
using Kaimo_File_Server.Core.Storage;
using Kaimo_File_Server.Infrastructure.Services;

namespace Kaimo_File_Server.Web.Components.ViewModels;

/// <summary>Host system information (network/storage/memory), storage-pool naming and public IP.</summary>
public partial class SettingsViewModel
{
    // ── System Info (IP / Storage / RAM) ──

    public string HostName { get; private set; } = "";
    public IReadOnlyList<NetworkAddressInfo> NetworkAddresses { get; private set; } = [];
    public IReadOnlyList<StorageUsageInfo> StorageUsages { get; private set; } = [];
    public MemoryUsageInfo? MemoryUsage { get; private set; }

    /// <summary>
    /// Editable friendly names for the storage pools, keyed by the normalized pool
    /// path. Every displayed pool has an entry (blank = fall back to the path's
    /// final component). Persisted under <see cref="StoragePoolNaming.ConfigKey"/>.
    /// </summary>
    public Dictionary<string, string> PoolNames { get; private set; } = new();

    /// <summary>The label shown for a pool: the custom name if set, else derived.</summary>
    public string ResolvePoolName(string poolPath)
        => StoragePoolNaming.Resolve(PoolNames, poolPath);

    /// <summary>The normalized map key for a pool path (used for two-way binding).</summary>
    public static string PoolNameKey(string poolPath)
        => StoragePoolNaming.NormalizeKey(poolPath);

    /// <summary>The path-derived fallback name, shown as the input placeholder.</summary>
    public static string PoolDerivedName(string poolPath)
        => StoragePoolNaming.DerivedName(poolPath);

    /// <summary>The server's public IP, once resolved. Null = not (yet) resolved.</summary>
    public string? PublicIp { get; private set; }

    /// <summary>True while the public IP is being fetched from the echo service.</summary>
    public bool PublicIpLoading { get; private set; }

    /// <summary>Re-samples host network, storage and memory information (local, instant).</summary>
    public void RefreshSystemInfo()
    {
        if (!CanManageSettings) return;
        RefreshNetworkInfo();
        RefreshStorageInfo();
        RefreshMemoryInfo();
    }

    public void RefreshNetworkInfo()
    {
        if (!CanManageSettings) return;
        HostName = _sysInfo.HostName;
        NetworkAddresses = _sysInfo.GetNetworkAddresses();
    }

    public void RefreshStorageInfo()
    {
        if (!CanManageSettings) return;
        StorageUsages = _sysInfo.GetStorageUsage();

        // Keep an editable entry for every visible pool so the rename inputs can
        // bind to the map; preserve any unsaved edits already typed.
        foreach (var usage in StorageUsages)
        {
            var key = StoragePoolNaming.NormalizeKey(usage.StoragePath);
            if (!PoolNames.ContainsKey(key))
                PoolNames[key] = "";
        }
    }

    public void RefreshMemoryInfo()
    {
        if (!CanManageSettings) return;
        MemoryUsage = _sysInfo.GetMemoryUsage();
    }

    /// <summary>
    /// Loads the persisted custom pool names and ensures every currently visible
    /// pool has an (at least blank) entry so the editor can bind to it.
    /// </summary>
    private async Task LoadPoolNamesAsync()
    {
        PoolNames = await _config.GetAsync(
            StoragePoolNaming.ConfigKey, new Dictionary<string, string>());

        foreach (var usage in StorageUsages)
        {
            var key = StoragePoolNaming.NormalizeKey(usage.StoragePath);
            if (!PoolNames.ContainsKey(key))
                PoolNames[key] = "";
        }
    }

    /// <summary>
    /// Persists the custom pool names. Blank entries are dropped so a pool falls
    /// back to its path-derived name. The underlying pool paths are never changed.
    /// </summary>
    public async Task<bool> SavePoolNamesAsync()
    {
        ErrorMessage = null;
        SuccessMessage = null;

        if (!CanManageSettings)
        {
            ErrorMessage = Resources.Web_Settings_NoPermissionChange;
            return false;
        }

        var toStore = PoolNames
            .Where(kv => !string.IsNullOrWhiteSpace(kv.Value))
            .ToDictionary(kv => kv.Key, kv => kv.Value.Trim());

        try
        {
            await _config.SetAsync(StoragePoolNaming.ConfigKey, toStore);
            _logger.LogInformation(
                "Storage pool names saved ({Count} custom name(s))", toStore.Count);
            SuccessMessage = Resources.Web_Settings_Storage_PoolNamesSaved;
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to save storage pool names");
            ErrorMessage = Resources.Web_Settings_Storage_PoolNamesSaveFailed;
            return false;
        }
    }

    // ── User home folders ──

    /// <summary>Pool that holds the home-folder share, or <c>null</c> while home folders are not set up.</summary>
    public string? HomesPoolPath { get; private set; }

    /// <summary>Pool chosen in the setup form (only used while <see cref="HomesPoolPath"/> is null).</summary>
    public string HomesSelectedPool { get; set; } = "";

    /// <summary>Global switch: when off, no user has a home folder (per-user settings are kept).</summary>
    public bool HomesEnabled { get; private set; }

    private async Task LoadHomesAsync()
    {
        var share = _homes is null ? null : await _homes.GetHomesShareAsync();
        HomesPoolPath = share is null ? null : Path.GetDirectoryName(share.Path);
        HomesEnabled = share?.IsEnabled == true;
        HomesSelectedPool = StorageUsages.FirstOrDefault()?.StoragePath ?? "";
    }

    /// <summary>Enables or disables home folders for every user at once.</summary>
    public async Task<bool> SetHomesEnabledAsync(bool enabled)
    {
        ErrorMessage = null;
        SuccessMessage = null;

        if (!CanManageSettings || _homes is null)
        {
            ErrorMessage = Resources.Web_Settings_NoPermissionChange;
            return false;
        }

        try
        {
            await _homes.SetGloballyEnabledAsync(enabled);
            SuccessMessage = enabled
                ? Resources.ResourceManager.GetString("Web_Settings_Homes_EnabledSaved") ?? "Home folders are enabled for all users."
                : Resources.ResourceManager.GetString("Web_Settings_Homes_DisabledSaved") ?? "Home folders are disabled for all users.";
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Switching home folders failed");
            ErrorMessage = Resources.ResourceManager.GetString("Web_Settings_Homes_ConfigureFailed")
                ?? "Home folders could not be set up.";
            return false;
        }
        finally
        {
            await LoadHomesAsync();
        }
    }

    /// <summary>
    /// Creates the home-folder share on the selected pool. The pool is fixed afterwards;
    /// existing homes are never moved.
    /// </summary>
    public async Task<bool> ConfigureHomesAsync()
    {
        ErrorMessage = null;
        SuccessMessage = null;

        if (!CanManageSettings || _homes is null)
        {
            ErrorMessage = Resources.Web_Settings_NoPermissionChange;
            return false;
        }

        // Only a pool the server actually discovered may be chosen.
        var pool = StorageUsages.FirstOrDefault(u =>
            string.Equals(u.StoragePath, HomesSelectedPool, StringComparison.Ordinal))?.StoragePath;
        if (pool is null)
        {
            ErrorMessage = Resources.Web_Error_StoragePoolRequired;
            return false;
        }

        try
        {
            var username = (await _authState.GetAuthenticationStateAsync()).User.Identity?.Name;
            var actor = string.IsNullOrEmpty(username) ? null : await _userContextFactory.CreateByUsernameAsync(username);
            if (actor is null)
            {
                ErrorMessage = Resources.Web_Settings_NoPermissionChange;
                return false;
            }

            var result = await _homes.ConfigureAsync(pool, actor.User.Id);
            if (result != HomeConfigureResult.Ok)
            {
                ErrorMessage = result switch
                {
                    HomeConfigureResult.NameTaken => Resources.ResourceManager.GetString("Web_Settings_Homes_NameTaken")
                        ?? "A share named \"users\" already exists. Rename it first.",
                    HomeConfigureResult.DestinationIsFile => Resources.Web_Error_StoragePoolDestinationExists,
                    _ => Resources.ResourceManager.GetString("Web_Settings_Homes_AlreadyConfigured")
                        ?? "Home folders are already set up.",
                };
                await LoadHomesAsync();
                return false;
            }

            _logger.LogInformation("Home folders set up on pool '{Pool}'", pool);
            SuccessMessage = Resources.ResourceManager.GetString("Web_Settings_Homes_Configured")
                ?? "Home folders are set up.";
            await LoadHomesAsync();
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Setting up home folders failed");
            ErrorMessage = Resources.ResourceManager.GetString("Web_Settings_Homes_ConfigureFailed")
                ?? "Home folders could not be set up.";
            return false;
        }
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
}

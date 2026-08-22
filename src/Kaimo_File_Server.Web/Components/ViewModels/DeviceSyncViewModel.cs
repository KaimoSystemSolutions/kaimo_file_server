using Kaimo_File_Server.Core.Domain.ClientSync;
using Kaimo_File_Server.Core.Domain.Identity;
using Kaimo_File_Server.Core.Repositories;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.Extensions.Logging;

namespace Kaimo_File_Server.Web.Components.ViewModels;

/// <summary>
/// Backs the read-only "My Devices &amp; Sync" page. Sync connections are created
/// and managed only by the client apps; this page merely shows the signed-in
/// user's connected devices/instances and, for reference, the folder connections
/// each one has configured. It performs no mutations.
/// </summary>
public sealed class DeviceSyncViewModel
{
    private readonly AuthenticationStateProvider _authState;
    private readonly IUserContextFactory _userContextFactory;
    private readonly ISyncDeviceRepository _devices;
    private readonly IDeviceSyncProfileRepository _profiles;
    private readonly IShareRepository _shares;
    private readonly ILogger<DeviceSyncViewModel> _logger;

    public DeviceSyncViewModel(
        AuthenticationStateProvider authState,
        IUserContextFactory userContextFactory,
        ISyncDeviceRepository devices,
        IDeviceSyncProfileRepository profiles,
        IShareRepository shares,
        ILogger<DeviceSyncViewModel> logger)
    {
        _authState = authState;
        _userContextFactory = userContextFactory;
        _devices = devices;
        _profiles = profiles;
        _shares = shares;
        _logger = logger;
    }

    public bool IsLoading { get; private set; } = true;
    public bool LoadFailed { get; private set; }

    public IReadOnlyList<SyncDevice> Devices { get; private set; } = [];
    public IReadOnlyDictionary<Guid, List<DeviceSyncProfile>> ProfilesByDevice { get; private set; }
        = new Dictionary<Guid, List<DeviceSyncProfile>>();

    private readonly Dictionary<Guid, string> _shareNames = new();

    public async Task LoadAsync()
    {
        IsLoading = true;
        LoadFailed = false;
        try
        {
            var user = await GetCurrentUserAsync();
            if (user is null)
            {
                Reset();
                return;
            }

            Devices = await _devices.GetByUserAsync(user.User.Id);

            var byDevice = new Dictionary<Guid, List<DeviceSyncProfile>>();
            _shareNames.Clear();
            foreach (var device in Devices)
            {
                var profiles = await _profiles.GetByDeviceAsync(device.Id);
                byDevice[device.Id] = profiles;
                foreach (var profile in profiles)
                    await CacheShareNameAsync(profile.ShareId);
            }
            ProfilesByDevice = byDevice;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load device sync configuration");
            LoadFailed = true;
        }
        finally
        {
            IsLoading = false;
        }
    }

    /// <summary>Resolves a share id to its display name for rendering.</summary>
    public string ShareName(Guid shareId)
        => _shareNames.TryGetValue(shareId, out var name) ? name : shareId.ToString();

    private async Task CacheShareNameAsync(Guid shareId)
    {
        if (_shareNames.ContainsKey(shareId))
            return;
        var share = await _shares.GetByIdAsync(shareId);
        _shareNames[shareId] = share?.Name ?? shareId.ToString();
    }

    private void Reset()
    {
        Devices = [];
        ProfilesByDevice = new Dictionary<Guid, List<DeviceSyncProfile>>();
        _shareNames.Clear();
    }

    private async Task<UserContext?> GetCurrentUserAsync()
    {
        var state = await _authState.GetAuthenticationStateAsync();
        var username = state.User.Identity?.Name;
        if (string.IsNullOrEmpty(username)) return null;
        return await _userContextFactory.CreateByUsernameAsync(username);
    }
}

using Kaimo_File_Server.Core.Domain.ClientSync;
using Kaimo_File_Server.Core.Domain.Identity;
using Kaimo_File_Server.Core.Repositories;
using Kaimo_File_Server.Core.Security;
using Kaimo_File_Server.Core.Services;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.Extensions.Logging;

namespace Kaimo_File_Server.Web.Components.ViewModels;

/// <summary>
/// Backs the "Client devices" settings tab. It lets an operator holding
/// <see cref="ManagementPermission.ManageClientDevices"/> review every user's
/// registered devices (their connected apps and instances, plus each device's
/// synced-folder selections) and revoke stale or unused ones.
///
/// Revoking a device is the single mutation offered: it marks the device revoked,
/// revokes all of its refresh tokens, and drops its sync selections. Combined with
/// the per-request device check in the client API, this invalidates both the
/// refresh token and any outstanding access token at once.
///
/// Retention: to keep the list from filling with entries that can no longer reach the
/// server, <see cref="LoadAsync"/> prunes retired registrations before reading. A device
/// is retired once it has been revoked for longer than <see cref="RevokedRetentionDays"/>,
/// or has not checked in for <see cref="InactivityRetentionDays"/> — by then its refresh
/// token (default 30-day lifetime, rotated on use) has long expired, so it could only come
/// back by signing in again, which creates a fresh registration anyway.
/// </summary>
public sealed class ClientDeviceAdminViewModel
{
    /// <summary>
    /// Days a revoked device is kept before it is pruned. A short grace period leaves the
    /// revocation visible to an operator who wants to confirm it, then clears it out.
    /// </summary>
    public const int RevokedRetentionDays = 30;

    /// <summary>
    /// Days without a single authenticated request after which a device is pruned. Set well
    /// beyond the 30-day refresh-token lifetime so only genuinely dead devices — ones that
    /// would have to re-register to return — are removed.
    /// </summary>
    public const int InactivityRetentionDays = 90;

    private readonly AuthenticationStateProvider _authState;
    private readonly IUserContextFactory _userContextFactory;
    private readonly IManagementAuthService _mgmtAuth;
    private readonly ISyncDeviceRepository _devices;
    private readonly IDeviceSyncProfileRepository _profiles;
    private readonly IRefreshTokenRepository _refreshTokens;
    private readonly IShareRepository _shares;
    private readonly TimeProvider _clock;
    private readonly ILogger<ClientDeviceAdminViewModel> _logger;

    public ClientDeviceAdminViewModel(
        AuthenticationStateProvider authState,
        IUserContextFactory userContextFactory,
        IManagementAuthService mgmtAuth,
        ISyncDeviceRepository devices,
        IDeviceSyncProfileRepository profiles,
        IRefreshTokenRepository refreshTokens,
        IShareRepository shares,
        TimeProvider clock,
        ILogger<ClientDeviceAdminViewModel> logger)
    {
        _authState = authState;
        _userContextFactory = userContextFactory;
        _mgmtAuth = mgmtAuth;
        _devices = devices;
        _profiles = profiles;
        _refreshTokens = refreshTokens;
        _shares = shares;
        _clock = clock;
        _logger = logger;
    }

    public bool IsLoading { get; private set; } = true;
    public bool LoadFailed { get; private set; }

    /// <summary>Whether the current user may view and revoke client devices at all.</summary>
    public bool CanManage { get; private set; }

    public IReadOnlyList<SyncDevice> Devices { get; private set; } = [];
    public IReadOnlyDictionary<Guid, List<DeviceSyncProfile>> ProfilesByDevice { get; private set; }
        = new Dictionary<Guid, List<DeviceSyncProfile>>();

    private readonly Dictionary<Guid, string> _ownerNames = new();
    private readonly Dictionary<Guid, string> _shareNames = new();

    private UserContext? _actor;

    public async Task LoadAsync()
    {
        IsLoading = true;
        LoadFailed = false;
        try
        {
            _actor = await GetCurrentUserAsync();
            CanManage = _actor is not null
                && await _mgmtAuth.HasAnyPermissionAsync(_actor, ManagementPermission.ManageClientDevices);
            if (!CanManage)
            {
                Reset();
                return;
            }

            await PruneRetiredDevicesAsync();

            Devices = await _devices.GetAllAsync();

            var byDevice = new Dictionary<Guid, List<DeviceSyncProfile>>();
            _shareNames.Clear();
            _ownerNames.Clear();
            foreach (var device in Devices)
            {
                await CacheOwnerNameAsync(device.UserId);
                var profiles = await _profiles.GetByDeviceAsync(device.Id);
                byDevice[device.Id] = profiles;
                foreach (var profile in profiles)
                    await CacheShareNameAsync(profile.ShareId);
            }
            ProfilesByDevice = byDevice;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load client device administration");
            LoadFailed = true;
        }
        finally
        {
            IsLoading = false;
        }
    }

    /// <summary>
    /// Removes registrations that can no longer reach the server, so retired devices do not
    /// pile up in the list. Runs opportunistically whenever an operator opens the page;
    /// failures here are swallowed so a cleanup hiccup never blocks viewing the devices.
    /// </summary>
    private async Task PruneRetiredDevicesAsync()
    {
        try
        {
            var now = _clock.GetUtcNow().UtcDateTime;
            var removed = await _devices.DeleteRetiredAsync(
                revokedBeforeUtc: now.AddDays(-RevokedRetentionDays),
                inactiveBeforeUtc: now.AddDays(-InactivityRetentionDays));
            if (removed > 0)
                _logger.LogInformation(
                    "Pruned {Count} retired client device(s) (revoked > {RevokedDays}d ago or unseen > {InactiveDays}d)",
                    removed, RevokedRetentionDays, InactivityRetentionDays);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to prune retired client devices; continuing to load the list");
        }
    }

    /// <summary>
    /// Revokes a device: marks it revoked, revokes its refresh tokens, and removes
    /// its sync selections. Re-checks the permission so a stale page cannot mutate.
    /// Returns true on success. The caller should reload afterwards.
    /// </summary>
    public async Task<bool> RevokeDeviceAsync(Guid deviceId)
    {
        if (_actor is null
            || !await _mgmtAuth.HasAnyPermissionAsync(_actor, ManagementPermission.ManageClientDevices))
            return false;

        var device = await _devices.GetByIdAsync(deviceId);
        if (device is null)
            return false;

        var now = _clock.GetUtcNow().UtcDateTime;
        try
        {
            if (device.IsActive)
            {
                device.RevokedAtUtc = now;
                await _devices.UpdateAsync(device);
            }
            await _refreshTokens.RevokeAllForDeviceAsync(device.Id, now);

            // Drop the device's sync selections so a re-registration starts clean.
            var profiles = await _profiles.GetByDeviceAsync(device.Id);
            foreach (var profile in profiles)
                await _profiles.DeleteAsync(profile.Id);

            _logger.LogInformation(
                "Client device {DeviceId} ({DisplayName}) revoked by {Actor}",
                device.Id, device.DisplayName, _actor.User.Username);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to revoke client device {DeviceId}", deviceId);
            return false;
        }
    }

    /// <summary>Owner display name for a device's user id.</summary>
    public string OwnerName(Guid userId)
        => _ownerNames.TryGetValue(userId, out var name) ? name : userId.ToString();

    /// <summary>Resolves a share id to its display name for rendering.</summary>
    public string ShareName(Guid shareId)
        => _shareNames.TryGetValue(shareId, out var name) ? name : shareId.ToString();

    private async Task CacheOwnerNameAsync(Guid userId)
    {
        if (_ownerNames.ContainsKey(userId))
            return;
        var owner = await _userContextFactory.CreateByUserIdAsync(userId);
        _ownerNames[userId] = owner?.User.Username ?? userId.ToString();
    }

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
        _ownerNames.Clear();
    }

    private async Task<UserContext?> GetCurrentUserAsync()
    {
        var state = await _authState.GetAuthenticationStateAsync();
        var username = state.User.Identity?.Name;
        if (string.IsNullOrEmpty(username)) return null;
        return await _userContextFactory.CreateByUsernameAsync(username);
    }
}

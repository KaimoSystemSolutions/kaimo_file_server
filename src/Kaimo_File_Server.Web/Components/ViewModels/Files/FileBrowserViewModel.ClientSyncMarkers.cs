using Kaimo_File_Server.Core.Domain;
using Kaimo_File_Server.Core.Domain.Identity;
using Kaimo_File_Server.Core.Helpers;
using Kaimo_File_Server.Core.Security.Management;
using Kaimo_File_Server.Core.Services.CloudSync;
using Microsoft.EntityFrameworkCore;

namespace Kaimo_File_Server.Web.Components.ViewModels;

public partial class FileBrowserViewModel
{
    // Enabled client-device sync selections on the loaded share that the viewer may see.
    // Loaded per share; replaced (never mutated) so render-time reads need no lock.
    private List<(string Root, Guid DeviceId, ClientSyncDevice Device)> _clientSyncs = [];

    /// <summary>
    /// Whether the viewer holds the global <see cref="ManagementPermission.ManageClientDevices"/>
    /// right, so the client-sync emblem may jump to the device administration.
    /// </summary>
    public bool CanManageClientDevices { get; private set; }

    // The server only knows the configured selection (not per-file device state), so the
    // emblem says "this folder is synced to device X", never "pending" or "failed".
    private async Task LoadClientSyncsAsync(Guid shareId, UserContext viewer)
    {
        _clientSyncs = [];
        CanManageClientDevices = false;
        if (!Capabilities.HasClientSyncMarkers)
            return;

        try
        {
            // Own devices for everyone; every device only for a client-device admin (the same
            // global right the device administration page requires).
            var seesAll = await _mgmtAuth.HasGlobalPermissionAsync(viewer, ManagementPermission.ManageClientDevices);
            CanManageClientDevices = seesAll;
            var viewerId = viewer.User.Id;

            await using var db = await _dbFactory.CreateDbContextAsync();
            var rows = await (
                from profile in db.DeviceSyncProfiles
                join device in db.SyncDevices on profile.DeviceId equals device.Id
                join owner in db.Users on device.UserId equals owner.Id
                where profile.ShareId == shareId && profile.Enabled && device.RevokedAtUtc == null
                      && (seesAll || device.UserId == viewerId)
                select new { profile.RelativePath, profile.Mode, profile.DeviceId, device.DisplayName, device.UserId, owner.Name, owner.Username })
                .ToListAsync();

            // An overlapping load of another share has taken over meanwhile: its own
            // selections are (or will be) loaded, these would mark the wrong folders.
            if (CurrentShare?.Id != shareId)
                return;

            _clientSyncs = rows
                .Select(row => (
                    Root: ShareRelativePath.Normalize(row.RelativePath),
                    row.DeviceId,
                    Device: new ClientSyncDevice(
                        row.DisplayName,
                        row.UserId == viewerId ? null : (string.IsNullOrWhiteSpace(row.Name) ? row.Username : row.Name),
                        row.Mode)))
                // Own devices first, then by owner and device name, so the tooltip reads stably.
                .OrderBy(sync => sync.Device.OwnerName is not null)
                .ThenBy(sync => sync.Device.OwnerName, StringComparer.CurrentCultureIgnoreCase)
                .ThenBy(sync => sync.Device.DeviceName, StringComparer.CurrentCultureIgnoreCase)
                .ToList();
        }
        catch (Exception ex)
        {
            // A decoration only: the listing must never fail because of it.
            _logger.LogWarning(ex, "Failed to load client device syncs for share {ShareId}", shareId);
        }
    }

    /// <inheritdoc />
    public ClientSyncMarker? GetClientSyncMarker(FileMetadata entry)
    {
        var syncs = _clientSyncs;
        if (syncs.Count == 0)
            return null;

        // Ordinal like the cloud-sync markers: shares live on case-sensitive filesystems.
        var rel = ShareRelativePath.Normalize(ShareRelativeOf(entry));
        var devices = syncs
            .Where(sync => sync.Root.Length == 0
                           || string.Equals(rel, sync.Root, StringComparison.Ordinal)
                           || rel.StartsWith(sync.Root + "/", StringComparison.Ordinal))
            // One line per device; with nested selections the innermost one decides its mode.
            .GroupBy(sync => sync.DeviceId)
            .Select(group => group.MaxBy(sync => sync.Root.Length).Device)
            .ToList();
        if (devices.Count == 0)
            return null;

        var mode = devices.All(d => d.Mode == devices[0].Mode) ? devices[0].Mode : SyncMode.TwoWay;
        return new ClientSyncMarker(mode, devices);
    }
}

using Kaimo_File_Server.Core.Services.CloudSync;
using Kaimo_File_Server.Web.Components.ViewModels;

namespace Kaimo_File_Server.Web.Components.Pages.Files.FileBrowser;

public partial class FileBrowser
{
// ========== Client device sync emblem ==========

    // Same direction glyphs as the cloud-sync emblem; the violet tint (not --info) tells the
    // two apart when a folder carries both. Violet is distinct from info blue, warning amber,
    // danger red and the (default green) accent in both themes.
    private const string ClientSyncColor = "var(--sync-client)";

    private bool CanOpenClientDevices
        => VM is FileBrowserViewModel { CanManageClientDevices: true };

    // One line per device: "Synced to client devices:" + "Your device "Laptop": downloaded …".
    private static string ClientSyncTooltip(ClientSyncMarker marker)
        => string.Join('\n', marker.Devices
            .Select(device => device.OwnerName is null
                ? string.Format(T("Web_ClientSync_OwnDeviceLine"), device.DeviceName, ClientSyncModeText(device.Mode))
                : string.Format(T("Web_ClientSync_UserDeviceLine"), device.DeviceName, device.OwnerName, ClientSyncModeText(device.Mode)))
            .Prepend(T("Web_ClientSync_Tooltip")));

    // Arrows read from the browsed server folder, like the cloud-sync emblem beside it: a device
    // Pull (server → device) sends data out (up arrow), a device Push brings it in (down arrow).
    private static string ClientSyncIcon(SyncMode mode) => mode switch
    {
        SyncMode.Pull => "sync-push.svg",
        SyncMode.Push => "sync-pull.svg",
        _ => "sync-twoway.svg"
    };

    private static string ClientSyncModeText(SyncMode mode) => mode switch
    {
        SyncMode.Pull => T("Web_ClientSync_Mode_Pull"),
        SyncMode.Push => T("Web_ClientSync_Mode_Push"),
        _ => T("Web_ClientSync_Mode_TwoWay")
    };

    private void OpenClientDevices()
    {
        if (CanOpenClientDevices)
            Nav.NavigateTo("/settings?tab=ClientDevices");
    }
}

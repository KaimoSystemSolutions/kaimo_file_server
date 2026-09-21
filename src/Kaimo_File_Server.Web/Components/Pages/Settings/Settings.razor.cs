using Microsoft.AspNetCore.Components;

namespace Kaimo_File_Server.Web.Components.Pages.Settings;

public partial class Settings
{
    private static string R(string key)
        => Kaimo_File_Server.Core.Language.Resources.ResourceManager.GetString(key) ?? key;

    /// <summary>
    /// Deep-link into a specific settings tab, e.g. <c>/settings?tab=logging</c>.
    /// Used by the global search so a settings result opens straight on its tab.
    /// Ignored when the tab is missing/unknown or the user lacks permission for it.
    /// </summary>
    [Parameter]
    [SupplyParameterFromQuery(Name = "tab")]
    public string? Tab { get; set; }

    private SettingsTab _activeTab = SettingsTab.Language;
    private bool _initialLoadComplete;

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (!firstRender)
            return;

        await VM.LoadAsync();

        if (!VM.CanManageSettings)
        {
            if (VM.CanViewLogs)
                _activeTab = SettingsTab.Logging;
            else if (VM.CanManageDataServices)
                _activeTab = SettingsTab.DataServices;
            else if (VM.CanManageCertificates)
                _activeTab = SettingsTab.Certificate;
            else if (VM.CanManageBackups)
                _activeTab = SettingsTab.Backup;
            else if (VM.CanManageClientDevices)
                _activeTab = SettingsTab.ClientDevices;
        }

        _initialLoadComplete = true;

        // Honor a ?tab= deep-link once permissions are known; SwitchTab also runs
        // any per-tab data load (e.g. search state) the target tab needs.
        if (TryParseTab(Tab, out var requested) && IsTabPermitted(requested))
            await SwitchTab(requested);

        StateHasChanged();
    }

    protected override async Task OnParametersSetAsync()
    {
        // Query change while already on the page (permissions already resolved).
        if (_initialLoadComplete
            && TryParseTab(Tab, out var requested)
            && IsTabPermitted(requested)
            && requested != _activeTab)
            await SwitchTab(requested);
    }

    private static bool TryParseTab(string? value, out SettingsTab tab)
    {
        tab = default;
        return !string.IsNullOrWhiteSpace(value)
               && Enum.TryParse(value, ignoreCase: true, out tab)
               && Enum.IsDefined(tab);
    }

    /// <summary>Mirrors the per-tab permission gates in Settings.razor.</summary>
    private bool IsTabPermitted(SettingsTab tab) => tab switch
    {
        SettingsTab.Logging => VM.CanManageSettings || VM.CanViewLogs,
        SettingsTab.DataServices => VM.CanManageDataServices,
        SettingsTab.Certificate => VM.CanManageCertificates,
        SettingsTab.Backup => VM.CanManageBackups,
        SettingsTab.ClientDevices => VM.CanManageClientDevices,
        _ => VM.CanManageSettings,
    };

    private async Task SwitchTab(SettingsTab tab)
    {
        _activeTab = tab;
        VM.ClearMessages();

        if (tab == SettingsTab.Network)
            await VM.LoadPublicIpAsync();
        else if (tab == SettingsTab.Search)
        {
            var loadTask = VM.LoadSearchStateAsync();
            StateHasChanged();
            await loadTask;
        }
        else if (tab == SettingsTab.Certificate)
            await VM.LoadCertificateStateAsync();

        StateHasChanged();
    }
}

internal enum SettingsTab
{
    Language,
    ContextMenu,
    PasswordPolicy,
    Network,
    Storage,
    Memory,
    DataServices,
    Search,
    CloudAccess,
    ShareLinks,
    Logging,
    Certificate,
    Backup,
    ClientDevices
}

namespace Kaimo_File_Server.Web.Components.Pages.Settings;

public partial class Settings
{
    private static string R(string key)
        => Kaimo_File_Server.Core.Language.Resources.ResourceManager.GetString(key) ?? key;

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
        }

        _initialLoadComplete = true;
        StateHasChanged();
    }

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
    Logging,
    Certificate
}

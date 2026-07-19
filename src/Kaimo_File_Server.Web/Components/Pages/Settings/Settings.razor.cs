namespace Kaimo_File_Server.Web.Components.Pages.Settings;

public partial class Settings
{
    private SettingsTab _activeTab = SettingsTab.Language;

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (!firstRender)
            return;

        await VM.LoadAsync();

        if (!VM.CanManageSettings)
        {
            if (VM.CanManageDataServices)
                _activeTab = SettingsTab.DataServices;
            else if (VM.CanManageCertificates)
                _activeTab = SettingsTab.Certificate;
        }

        StateHasChanged();
    }

    private async Task SwitchTab(SettingsTab tab)
    {
        _activeTab = tab;
        VM.ClearMessages();

        if (tab == SettingsTab.Network)
            await VM.LoadPublicIpAsync();
        else if (tab == SettingsTab.Search)
            await VM.LoadSearchStateAsync();
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
    Logging,
    Certificate
}

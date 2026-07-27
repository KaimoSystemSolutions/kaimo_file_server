using Kaimo_File_Server.Core.Domain;
using Kaimo_File_Server.Web.Services;
using Microsoft.AspNetCore.Components;

namespace Kaimo_File_Server.Web.Components.Pages.Shares;

public partial class ShareList
{
    
    [SupplyParameterFromQuery]
    [Parameter]
    public string? SuccessfulConnection { get; set; }
    private bool _handledSuccessfulConnection;

    private ShareDetailTab _activeTab = ShareDetailTab.Settings;
    private string _aclEditorKey = "";
    private bool _accessLoaded;
    private bool _showExtendedShareInfo;
    

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (firstRender)
        {
            await VM.LoadAsync();
            StateHasChanged();
        }
        
        if (_handledSuccessfulConnection)
            return;

        if (string.IsNullOrWhiteSpace(SuccessfulConnection))
            return;

        if (VM.IsLoading)
            return;

        var share = VM.Shares.FirstOrDefault(s => s.Name == SuccessfulConnection);
        if (share is null)
            return;

        _handledSuccessfulConnection = true;

        HandleCardClick(share);
        SwitchTab(ShareDetailTab.Cloud);
        StateHasChanged();
        Toasts.Show("Successfully synced Cloud with Share", ToastType.Success);
    }
    
    private void HandleCardClick(ShareDefinition share)
    {
        if (VM.CanManageShare(share.Id))
        {
            _activeTab = ShareDetailTab.Settings;
            _accessLoaded = false;
            VM.SelectShare(share);
            StateHasChanged();
            return;
        }

        OpenShare(share.Name);
    }

    private async Task SwitchTab(ShareDetailTab tab)
    {
        if (VM.IsSelectedShareReadOnly && tab != ShareDetailTab.Settings)
            return;

        _activeTab = tab;

        if (tab == ShareDetailTab.Access && !_accessLoaded)
        {
            await VM.LoadAccessAsync();
            _accessLoaded = true;
        }

        if (tab == ShareDetailTab.Acl)
            _aclEditorKey = $"{VM.SelectedShare?.Name}_{DateTime.UtcNow.Ticks}";

        StateHasChanged();
    }

    private void ToggleCreate()
    {
        VM.DeselectShare();
        _activeTab = ShareDetailTab.Settings;
        _accessLoaded = false;
        VM.IsCreating = !VM.IsCreating;
        StateHasChanged();
    }

    private void ToggleExtendedShareInfo()
        => _showExtendedShareInfo = !_showExtendedShareInfo;

    private void OpenShare(string name) => Nav.NavigateTo($"/files/{name}");

    private void HandleShareDeleted()
    {
        _activeTab = ShareDetailTab.Settings;
        _accessLoaded = false;
        StateHasChanged();
    }
}

internal enum ShareDetailTab
{
    Settings,
    Access,
    Acl,
    Cloud
}

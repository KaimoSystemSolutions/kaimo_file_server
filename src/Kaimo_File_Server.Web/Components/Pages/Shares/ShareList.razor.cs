using Kaimo_File_Server.Core.Domain;
using Kaimo_File_Server.Web.Services;
using Microsoft.AspNetCore.Components;

namespace Kaimo_File_Server.Web.Components.Pages.Shares;

public partial class ShareList
{
    [SupplyParameterFromQuery(Name = "view")]
    [Parameter]
    public string? View { get; set; }

    [SupplyParameterFromQuery(Name = "connection")]
    [Parameter]
    public Guid? Connection { get; set; }

    private ShareKind _shareKind = ShareKind.Local;
    
    [SupplyParameterFromQuery]
    [Parameter]
    public string? SuccessfulConnection { get; set; }
    private bool _handledSuccessfulConnection;

    private ShareDetailTab _activeTab = ShareDetailTab.Settings;
    private string _aclEditorKey = "";
    private bool _showExtendedShareInfo;

    protected override void OnParametersSet()
        => _shareKind = string.Equals(View, "virtual", StringComparison.OrdinalIgnoreCase)
            ? ShareKind.Virtual
            : ShareKind.Local;
    

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
        StateHasChanged();
        Toasts.Show(Text("Web_ShareList_CloudConnected", "The cloud connection was added successfully."), ToastType.Success);
    }

    private void HandleCardClick(ShareDefinition share)
    {
        if (VM.CanManageShare(share.Id))
        {
            _activeTab = ShareDetailTab.Settings;
            VM.SelectShare(share);
            StateHasChanged();
            return;
        }

        OpenShare(share.Name);
    }

    private void SwitchTab(ShareDetailTab tab)
    {
        if (VM.IsSelectedShareReadOnly && tab != ShareDetailTab.Settings)
            return;

        _activeTab = tab;

        if (tab == ShareDetailTab.Acl)
            _aclEditorKey = $"{VM.SelectedShare?.Name}_{DateTime.UtcNow.Ticks}";

        StateHasChanged();
    }

    private void ToggleCreate()
    {
        VM.DeselectShare();
        _activeTab = ShareDetailTab.Settings;
        VM.IsCreating = !VM.IsCreating;
        StateHasChanged();
    }

    private void ToggleExtendedShareInfo()
        => _showExtendedShareInfo = !_showExtendedShareInfo;

    private void SelectShareKind(ShareKind kind) => _shareKind = kind;

    private static string Text(string key, string fallback)
        => Core.Language.Resources.ResourceManager.GetString(key) ?? fallback;

    private void OpenShare(string name) => Nav.NavigateTo($"/files/{name}");

    private void HandleShareDeleted()
    {
        _activeTab = ShareDetailTab.Settings;
        StateHasChanged();
    }
}

internal enum ShareKind
{
    Local,
    Virtual
}

internal enum ShareDetailTab
{
    Settings,
    Extra,
    Acl
}

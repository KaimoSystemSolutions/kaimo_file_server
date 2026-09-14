using Kaimo_File_Server.Web.Components.ViewModels;
using Microsoft.AspNetCore.Components;

namespace Kaimo_File_Server.Web.Components.Pages.Users;

public partial class UserList : IDisposable
{
    [Inject] private ProfileNavigator ProfileNav { get; set; } = default!;

    protected override void OnInitialized()
        => ProfileNav.SelfRequested += OnSelfRequestedAsync;

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (!firstRender)
            return;

        await VM.LoadAsync();
        // Honor a "view my profile" click made from another page before this one mounted.
        if (ProfileNav.ConsumePending())
            await VM.GoToSelfAsync();
        StateHasChanged();
    }

    // Fires when the sidebar requests the profile while this page is already open.
    private async Task OnSelfRequestedAsync()
    {
        await VM.GoToSelfAsync();
        await InvokeAsync(StateHasChanged);
    }

    private async Task SwitchTab(AdminTab tab)
    {
        await VM.SwitchTabAsync(tab);
        StateHasChanged();
    }

    public void Dispose()
        => ProfileNav.SelfRequested -= OnSelfRequestedAsync;
}

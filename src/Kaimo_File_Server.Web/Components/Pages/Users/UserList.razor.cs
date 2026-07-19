using Kaimo_File_Server.Web.Components.ViewModels;

namespace Kaimo_File_Server.Web.Components.Pages.Users;

public partial class UserList
{
    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (!firstRender)
            return;

        await VM.LoadAsync();
        StateHasChanged();
    }

    private async Task SwitchTab(AdminTab tab)
    {
        await VM.SwitchTabAsync(tab);
        StateHasChanged();
    }
}

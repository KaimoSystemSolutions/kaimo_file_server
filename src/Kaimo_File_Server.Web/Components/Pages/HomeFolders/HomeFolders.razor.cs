namespace Kaimo_File_Server.Web.Components.Pages.HomeFolders;

public partial class HomeFolders
{
    private bool _loaded;

    protected override async Task OnInitializedAsync()
    {
        await HomesVM.LoadAsync();
        _loaded = true;
    }

    private static string T(string key, string fallback)
        => Kaimo_File_Server.Core.Language.Resources.ResourceManager.GetString(key) ?? fallback;
}

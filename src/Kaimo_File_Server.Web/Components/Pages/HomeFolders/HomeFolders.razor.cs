using Microsoft.AspNetCore.Components;

namespace Kaimo_File_Server.Web.Components.Pages.HomeFolders;

public partial class HomeFolders
{
    /// <summary>Deep link into a tab, e.g. <c>/home-folders?tab=settings</c>.</summary>
    [Parameter]
    [SupplyParameterFromQuery(Name = "tab")]
    public string? TabQuery { get; set; }

    private bool _loaded;
    private HomeFoldersTab? _tab;
    private string? _settingsLoadError;

    protected override async Task OnInitializedAsync()
    {
        // Sequential: both view models share the circuit's DbContext.
        await HomesVM.LoadAsync();
        await SettingsVM.LoadHomeFolderSettingsAsync();
        _settingsLoadError = SettingsVM.ErrorMessage;
        _loaded = true;
    }

    // Blazor runs this after OnInitializedAsync has finished, and again on every ?tab= change.
    protected override void OnParametersSet()
        => _tab = ResolveTab(TabQuery, HomesVM.CanAccessPage, SettingsVM.CanManageSettings);

    /// <summary>
    /// The requested tab when the actor may open it, otherwise the first permitted one
    /// (Overview before Settings); <c>null</c> when neither is permitted.
    /// </summary>
    internal static HomeFoldersTab? ResolveTab(string? query, bool canOverview, bool canSettings)
    {
        var requested = Enum.TryParse<HomeFoldersTab>(query, ignoreCase: true, out var tab) && Enum.IsDefined(tab)
            ? tab
            : HomeFoldersTab.Overview;
        if (requested == HomeFoldersTab.Settings && canSettings)
            return HomeFoldersTab.Settings;
        if (canOverview)
            return HomeFoldersTab.Overview;
        return canSettings ? HomeFoldersTab.Settings : null;
    }

    private async Task SwitchTab(HomeFoldersTab tab)
    {
        // Settings changes (global switch, pool move) show up in the overview right away.
        if (tab == HomeFoldersTab.Overview)
            await HomesVM.LoadAsync();
        Nav.NavigateTo("/home-folders?tab=" + tab.ToString().ToLowerInvariant(), replace: true);
    }

    private static string T(string key, string fallback)
        => Kaimo_File_Server.Core.Language.Resources.ResourceManager.GetString(key) ?? fallback;
}

public enum HomeFoldersTab
{
    Overview,
    Settings,
}

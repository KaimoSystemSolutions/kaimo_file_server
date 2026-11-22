using Kaimo_File_Server.Web.Components.Search;
using Kaimo_File_Server.Web.DynamicHelpers;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.JSInterop;

namespace Kaimo_File_Server.Web.Components.Pages.Search;

/// <summary>
/// Right-click menu of the results page. It renders through the file browser's
/// <c>ContextMenu</c> component (same look), but with only the entries that apply to the
/// selected item; it is not part of the configurable file-browser menu.
/// </summary>
public partial class SearchResultsPage
{
    private SearchTreeNode? _menuNode;
    private (double X, double Y) _menuAt;
    private ElementReference _menuRef;
    private bool _menuNeedsReposition;

    private void OpenMenu(MouseEventArgs e, SearchTreeNode node)
    {
        Select(node);
        _menuNode = node;
        _menuAt = (e.ClientX, e.ClientY);
        _menuNeedsReposition = true;
    }

    private void CloseMenu() => _menuNode = null;

    private List<ContextMenuItem> MenuItems(SearchTreeNode node)
    {
        var builder = new ContextMenuBuilder().AddHeader(node.Name);
        builder.AddIf(CanPreview(node), T("Web_SearchResults_Preview", "Preview"), "eye.svg", async () =>
        {
            CloseMenu();
            await PreviewAsync(node);
        });
        builder.Add(ShowInFolderLabel(node), "folder.svg", () =>
        {
            ShowInFolder(node);
            return Task.CompletedTask;
        });
        return builder.Build();
    }

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        try
        {
            if (_menuNode is not null && _menuNeedsReposition)
            {
                _menuNeedsReposition = false;
                // Keeps a menu opened near the viewport edge fully on screen (same helper as the browser).
                await JS.InvokeVoidAsync("contextMenu.reposition", _menuRef);
            }
            if (_revealSelection)
            {
                _revealSelection = false;
                await JS.InvokeVoidAsync("kaimoGlobalSearch.revealTreeSelection");
            }
        }
        catch (JSDisconnectedException)
        {
        }
    }
}

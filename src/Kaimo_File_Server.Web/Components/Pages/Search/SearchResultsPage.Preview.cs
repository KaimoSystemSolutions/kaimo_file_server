using Kaimo_File_Server.Web.Components.Pages.Files.FileBrowser.components;
using Kaimo_File_Server.Web.Components.Search;
using Kaimo_File_Server.Web.Components.ViewModels;

namespace Kaimo_File_Server.Web.Components.Pages.Search;

/// <summary>
/// File preview on the results page. The hit's folder is loaded into a page-own browser view
/// model (never the circuit's shared one, whose undo history and listing belong to the file
/// browser), so the preview reads the file with the same ACL checks, size cap and download
/// ticket as in the browser, and a hit the index still lists but that is gone is reported.
/// </summary>
public partial class SearchResultsPage
{
    // One preview per browser: a FilePreview reads through the view model it was rendered with.
    private FileBrowserViewModel _shareBrowser = default!;
    private HomeFileBrowserViewModel _homeBrowser = default!;
    private FilePreview? _sharePreview;
    private FilePreview? _homePreview;

    // Construction is cheap; nothing is loaded until a preview is requested.
    private void InitPreviewBrowsers()
    {
        _shareBrowser = ActivatorUtilities.CreateInstance<FileBrowserViewModel>(Services);
        _homeBrowser = ActivatorUtilities.CreateInstance<HomeFileBrowserViewModel>(Services);
    }

    private async Task PreviewAsync(SearchTreeNode node)
    {
        if (!CanPreview(node))
            return;
        _error = null;

        FileBrowserViewModel browser;
        FilePreview? preview;
        string share;
        if (node.Share == SearchHitNavigation.HomeRoute)
        {
            if (!await _homeBrowser.InitializeAsync())
            {
                _error = T("Web_Home_Unavailable", "Your home folder is not available.");
                return;
            }
            browser = _homeBrowser;
            preview = _homePreview;
            share = _homeBrowser.HomeShareName!;
        }
        else
        {
            browser = _shareBrowser;
            preview = _sharePreview;
            share = node.Share;
        }

        await browser.LoadShareAsync(share, SearchHitNavigation.FolderOf(node.SharePath));
        var file = browser.Items.FirstOrDefault(i => !i.IsDirectory && i.Name == node.Name);
        if (file is null)
        {
            _error = browser.ErrorMessage
                ?? T("Web_SearchResults_Gone", "This file no longer exists. The search index will catch up shortly.");
            return;
        }

        if (preview is not null)
            await preview.OpenFilePreview(file);
    }

    private void DisposePreview()
    {
        _shareBrowser.Dispose();
        _homeBrowser.Dispose();
    }
}

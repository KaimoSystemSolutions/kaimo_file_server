using Kaimo_File_Server.Search;
using Kaimo_File_Server.Web.Components.Search;

namespace Kaimo_File_Server.Web.Components.Pages.Search;

/// <summary>
/// Narrowing the results: the refine field (file names of the loaded hits, or a full-text search
/// of the same scope whose hits must also be in the results), type, and modification date
/// (preset periods or a custom range). The main search is never repeated for this.
/// </summary>
public partial class SearchResultsPage
{
    private static readonly TimeSpan RefineDelay = TimeSpan.FromMilliseconds(400);

    private string _refineText = "";
    private SearchRefineMode _refineMode = SearchRefineMode.Name;
    private SearchTypeFilter _typeFilter;
    private SearchDateFilter _dateFilter;
    private DateOnly? _from;
    private DateOnly? _to;

    // Full-text mode: ids (and snippets) of the hits the refine term found in the same scope.
    private HashSet<string>? _contentHits;
    private Dictionary<string, string> _contentSnippets = [];
    private CancellationTokenSource? _refineCts;
    private bool _refining;

    private SearchResultsFilter Filter => new(
        _refineMode == SearchRefineMode.Name ? _refineText : "",
        _typeFilter, _dateFilter, _from, _to,
        _refineMode == SearchRefineMode.Content ? _contentHits : null, _homeLabels);

    /// <summary>Rebuilds the tree from the hits that pass the filter; selection and folding stay.</summary>
    private void ApplyFilter()
    {
        var filter = Filter;
        var now = DateTime.UtcNow;
        var shown = _allHits.Where(h => filter.Matches(h, now)).ToList();
        _shownHits = shown.Count;
        _roots = SearchResultTree.Build(shown, _homeLabels);
        _selected = _selectedKey is null ? null : FindNode(_selectedKey);
    }

    private void ResetFilter()
    {
        _refineCts?.Cancel();
        _refineText = "";
        _typeFilter = SearchTypeFilter.All;
        _dateFilter = SearchDateFilter.Any;
        _from = _to = null;
        _contentHits = null;
        _contentSnippets = [];
        _refining = false;
        ApplyFilter();
    }

    private async Task OnRefineChangedAsync()
    {
        if (_refineMode == SearchRefineMode.Name)
        {
            ApplyFilter();
            return;
        }
        await RefineByContentAsync(RefineDelay);
    }

    private async Task SetRefineModeAsync(SearchRefineMode mode)
    {
        if (_refineMode == mode)
            return;
        _refineMode = mode;
        _contentHits = null;
        _contentSnippets = [];
        if (mode == SearchRefineMode.Content)
            await RefineByContentAsync(TimeSpan.Zero);
        else
            ApplyFilter();
    }

    private async Task RefineByContentAsync(TimeSpan delay)
    {
        _refineCts?.Cancel();
        var cts = _refineCts = new CancellationTokenSource(SearchTimeout + delay);
        try
        {
            await Task.Delay(delay, cts.Token);
            _refining = true;
            await RefreshContentHitsAsync(cts.Token);
            ApplyFilter();
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested)
        {
            // Superseded by the next keystroke, or timed out: keep the previous state.
        }
        finally
        {
            if (_refineCts == cts)
                _refining = false;
        }
    }

    /// <summary>
    /// Full-text mode: searches the refine term over the page's scope through the index (names
    /// and contents, ACL-filtered like every search). Below two characters nothing is narrowed.
    /// </summary>
    private async Task RefreshContentHitsAsync(CancellationToken token)
    {
        var text = _refineText.Trim();
        if (_refineMode != SearchRefineMode.Content || text.Length < 2 || _user is null || !HasQuery)
        {
            _contentHits = null;
            _contentSnippets = [];
            return;
        }

        // Same hit limit as the main search, so a loaded hit that contains the term is found.
        // ponytail: above 1000 refine matches per target some can still be missed; combine both terms in one query if that matters
        var (hits, _, _) = await FindAsync(text, token);
        _contentHits = hits.Select(h => h.Id).ToHashSet();
        _contentSnippets = hits
            .Where(h => !string.IsNullOrEmpty(h.HighlightSnippet))
            .ToDictionary(h => h.Id, h => h.HighlightSnippet!);
    }

    private string RefinePlaceholder => _refineMode == SearchRefineMode.Name
        ? T("Web_SearchResults_RefineName_Placeholder", "Filter these results by name or path…")
        : T("Web_SearchResults_RefineContent_Placeholder", "Search the text of these results…");

    // In full-text mode the refine term's snippet explains why a hit is still shown.
    private string? SnippetOf(FileDocument hit)
        => _contentSnippets.GetValueOrDefault(hit.Id) ?? hit.HighlightSnippet;

    private string TypeLabel(SearchTypeFilter type) => type switch
    {
        SearchTypeFilter.Folders => T("Web_SearchResults_Type_Folders", "Folders"),
        SearchTypeFilter.Files => T("Web_SearchResults_Type_Files", "Files"),
        SearchTypeFilter.Documents => T("Web_SearchResults_Type_Documents", "Documents"),
        SearchTypeFilter.Images => T("Web_SearchResults_Type_Images", "Images"),
        SearchTypeFilter.Media => T("Web_SearchResults_Type_Media", "Audio & video"),
        SearchTypeFilter.Archives => T("Web_SearchResults_Type_Archives", "Archives"),
        _ => T("Web_SearchResults_Type_All", "All types"),
    };

    private string DateLabel(SearchDateFilter period) => period switch
    {
        SearchDateFilter.Day => T("Web_SearchResults_Modified_Day", "Last 24 hours"),
        SearchDateFilter.Week => T("Web_SearchResults_Modified_Week", "Last 7 days"),
        SearchDateFilter.Month => T("Web_SearchResults_Modified_Month", "Last 30 days"),
        SearchDateFilter.Year => T("Web_SearchResults_Modified_Year", "Last 12 months"),
        SearchDateFilter.Custom => T("Web_SearchResults_Modified_Custom", "Custom range"),
        _ => T("Web_SearchResults_Modified_Any", "Any date"),
    };
}

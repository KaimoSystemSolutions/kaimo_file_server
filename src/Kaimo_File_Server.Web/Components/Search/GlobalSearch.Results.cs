using Microsoft.AspNetCore.Components;

namespace Kaimo_File_Server.Web.Components.Search;

/// <summary>
/// The link between the search bar and the results page (/search): collapsible share sections,
/// "n more in …", "Show all results", and — on the results page itself — the searched scope
/// and term, read from the browser history entry (see <see cref="SearchResultsRequest"/>).
/// </summary>
public partial class GlobalSearch
{
    //  "results" → the scope of the search shown on /search (offered only there)
    private const string ScopeResults = "results";

    [Inject] private SearchResultsCoordinator ResultsCoordinator { get; set; } = default!;

    // On /search the bar drives the page below instead of listing file hits itself.
    private bool _onResultsPage;

    // The search shown on the results page; null anywhere else.
    private SearchResultsRequest? _resultsRequest;

    private void StartResultsLink() => ResultsCoordinator.Shown += OnResultsShown;

    private void StopResultsLink() => ResultsCoordinator.Shown -= OnResultsShown;

    private bool TryDetectResultsPage(string[] segs)
    {
        _resultsRequest = null;
        _onResultsPage = segs.Length == 1 && segs[0].Equals("search", StringComparison.OrdinalIgnoreCase);
        if (!_onResultsPage)
            return false;

        // A reload carries no state to .NET; the page reads it back and announces it (OnResultsShown).
        _resultsRequest = SearchResultsRequest.Parse(Nav.HistoryEntryState) ?? ResultsCoordinator.Current;
        _inBrowser = false;
        _contextShare = null;
        _contextSubPath = "";
        MirrorResultsScope();
        return true;
    }

    // The page shows a search (load, reload, back/forward): the bar mirrors term, scope and filter.
    private void OnResultsShown(SearchResultsRequest request)
        => _ = InvokeAsync(() =>
        {
            if (!_onResultsPage)
                return;
            _resultsRequest = request;
            _searchQuery = request.Query;
            MirrorResultsScope();
            StateHasChanged();
        });

    private void MirrorResultsScope()
    {
        _includeRecycleBin = _resultsRequest?.IncludeRecycleBin ?? _includeRecycleBin;
        _scope = _lastScope = _resultsRequest is { IsGlobal: false } ? ScopeResults : ScopeGlobal;
    }

    /// <summary>
    /// Scopes on the results page. Settings & tabs are always listed in the dropdown there, so
    /// the choice only decides where the page searches.
    /// </summary>
    private IEnumerable<(string Value, string Label)> ResultsPageScopeOptions()
    {
        if (_resultsRequest is { IsGlobal: false })
            yield return (ScopeResults, ResultsScopeLabel);
        yield return (ScopeGlobal, Text("Web_Search_AllShares", "All shares"));
        yield return (ScopePick, Text("Web_Search_Scope_PickedShare", "Selected shares"));
    }

    /// <summary>Hands term, scope and recycle-bin filter to the open results page.</summary>
    private void ForwardToResultsPage(List<(string? Share, string Sub)> targets)
    {
        if (_user is null || targets.Count == 0 || _searchQuery.Length < 2)
            return;
        // _resultsRequest keeps the page's original scope, so its option stays selectable.
        ResultsCoordinator.Ask(SearchResultsRequest.Create(_searchQuery, targets, _includeRecycleBin, _user.User.Id));
    }

    private string ResultsScopeLabel => _resultsRequest?.Describe(Text("Web_Search_AllShares", "All shares")) ?? "";

    // Collapses or expands one share section; the choice lasts until the page changes.
    private void ToggleShare(string share)
    {
        if (!_collapsedShares.Remove(share))
            _collapsedShares.Add(share);
        ApplyRecycleBinFilter();
    }

    private int TotalFileHits => _groups.Sum(g => g.Total);

    // A target returned the dropdown's full hit limit: the counts are lower bounds ("n+").
    private bool _hitsCapped;

    private string CountLabel(int count) => SearchHitGroups.CountLabel(count, _hitsCapped);

    // The full results page needs index targets; a virtual share is walked by name instead.
    private bool CanShowAllResults => _user is not null && !IsVirtualShareScope
        && _groups.Count > 0 && ResolvePlan().Targets.Count > 0;

    /// <summary>
    /// Where "n more" of a share leads: the searched folder when the scope is a folder of that
    /// share, otherwise the share itself.
    /// </summary>
    private SearchResultsTarget MoreTarget(SearchHitGroup group) => _user is null
        ? new SearchResultsTarget(group.Share, "")
        : SearchResultsRequest.FocusFor(group.Share, ResolvePlan().Targets, _user.User.Id);

    private string MoreLabel(SearchHitGroup group) => string.Format(
        MoreTarget(group).Path.Length > 0
            ? Text("Web_Search_MoreInFolder", "{0} more in this folder")
            : Text("Web_Search_MoreInShare", "{0} more in this share"),
        CountLabel(group.Total - group.Shown.Count));

    /// <summary>
    /// Opens the results page for the current search. The search term stays in the bar; the
    /// request travels in the history entry, so back/forward return to each search.
    /// </summary>
    private void ShowAllResults(SearchResultsTarget? focus = null)
    {
        var request = SearchResultsRequest.Create(_searchQuery, ResolvePlan().Targets, _includeRecycleBin, _user!.User.Id)
            with { Focus = focus };
        _dropdownVisible = false;
        ResetResults();
        // A search still running would fill the dropdown again on the results page.
        _searchCts?.Cancel();
        Nav.NavigateTo(SearchResultsRequest.Route, new NavigationOptions { HistoryEntryState = request.Serialize() });
    }
}

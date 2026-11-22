using Kaimo_File_Server.Core.Domain.Identity;
using Kaimo_File_Server.Core.Language;
using Kaimo_File_Server.Core.Repositories.Identity;
using Kaimo_File_Server.Core.Services.Search;
using Kaimo_File_Server.Search;
using Kaimo_File_Server.Web.Components.Search;
using Kaimo_File_Server.Web.Services.Files;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.JSInterop;

namespace Kaimo_File_Server.Web.Components.Pages.Search;

/// <summary>
/// The search results page (/search). Runs the search handed over in the browser history entry
/// (<see cref="SearchResultsRequest"/>) through <see cref="ISearchService"/>, which enforces the
/// ACLs, and shows every hit in a file tree that the filter row narrows down. The search bar
/// keeps driving it while the page is open (see SearchResultsPage.Live.cs).
/// </summary>
public partial class SearchResultsPage : IDisposable
{
    [Inject] private ISearchService SearchService { get; set; } = default!;
    [Inject] private IUserContextFactory UserContextFactory { get; set; } = default!;
    [Inject] private AuthenticationStateProvider AuthStateProvider { get; set; } = default!;
    [Inject] private NavigationManager Nav { get; set; } = default!;
    [Inject] private FileSelectionCoordinator Selection { get; set; } = default!;
    [Inject] private IServiceProvider Services { get; set; } = default!;
    [Inject] private IJSRuntime JS { get; set; } = default!;

    // Same cap as the search bar: a filename-fallback search holds a process-wide walk slot.
    private static readonly TimeSpan SearchTimeout = TimeSpan.FromSeconds(10);

    // The page lists everything a search finds, not the dropdown's first 50.
    // ponytail: one batch of up to 1000 readable hits per target, no paging; "n+" marks a capped result
    private const int MaxHitsPerTarget = SearchLimits.MaxRawScan;

    private UserContext? _user;
    private CancellationTokenSource? _cts;
    private SearchResultsRequest? _request;
    private bool _restoring = true;   // until the history entry has been read (first load / reload)
    private bool _loading;            // first results of a search: the tree is replaced by a spinner
    private bool _updating;           // a live update: the old tree stays visible meanwhile
    private string? _error;

    private List<FileDocument> _allHits = [];
    private bool _capped;            // the search stopped at its hit limit: there may be more
    private List<SearchTreeNode> _roots = [];
    private int _shownHits;
    private readonly HashSet<string> _collapsed = new(StringComparer.Ordinal);
    private SearchTreeNode? _selected;
    private string? _selectedKey;
    private bool _revealSelection;   // scroll the selected row into view after the next render

    private bool HasQuery => _request is { Query.Length: >= 2 };

    protected override async Task OnInitializedAsync()
    {
        InitPreviewBrowsers();
        StartLiveLink();
        await RestoreAsync();
    }

    /// <summary>
    /// Runs <paramref name="request"/>. A new search (<paramref name="keepView"/> false) resets
    /// selection and folding and applies its focus; a live update from the search bar keeps
    /// filters, folding and selection and leaves the old tree on screen until the new one is in.
    /// </summary>
    private async Task SearchAsync(SearchResultsRequest? request, bool keepView)
    {
        // The CTS is compared by reference, never through its Token: Dispose() disposes it
        // while a search may still be running.
        _cts?.Cancel();
        _cts?.Dispose();
        var cts = _cts = new CancellationTokenSource(SearchTimeout);
        var token = cts.Token;
        _request = request;
        _error = null;
        if (!keepView)
        {
            _selected = null;
            _selectedKey = null;
            _collapsed.Clear();
        }
        if (!HasQuery)
        {
            _allHits = [];
            _capped = false;
            _roots = [];
            return;
        }

        _loading = !keepView || _allHits.Count == 0;
        _updating = !_loading;
        try
        {
            _user ??= await CurrentUserAsync();
            if (_user is null)
                return;

            var (hits, capped) = await FindAsync(_request!.Query, token);
            await RefreshContentHitsAsync(token);
            if (_cts != cts)
                return;   // a newer search (or Dispose) replaced this one: its hits are stale
            _allHits = hits;
            _capped = capped;
            ApplyFilter();
            if (!keepView)
                ApplyFocus();
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            // A newer search replaced this one, or it ran into the timeout.
            if (_cts == cts)
                _error = T("Web_SearchResults_TimedOut", "The search took too long. Narrow it down and try again.");
        }
        finally
        {
            if (_cts == cts)
                _loading = _updating = false;
        }
    }

    /// <summary>
    /// Searches <paramref name="query"/> over the page's scope (main search and full-text refine).
    /// <c>Capped</c>: a target returned its full hit limit, so there may be more.
    /// </summary>
    private async Task<(List<FileDocument> Hits, bool Capped)> FindAsync(string query, CancellationToken token)
    {
        var perTarget = await Task.WhenAll(_request!.ToSearchTargets(_user!.User.Id).Select(t =>
            SearchService.SearchAsync(query, _user, t.Share, t.Sub, token, _request.IncludeRecycleBin, MaxHitsPerTarget)));
        var hits = perTarget.SelectMany(h => h)
            .Select(h => SearchHitNavigation.ToWebHit(h, _user.User.Id))
            .DistinctBy(h => h.Id)
            .ToList();
        return (hits, perTarget.Any(h => h.Count >= MaxHitsPerTarget));
    }

    private async Task<UserContext?> CurrentUserAsync()
    {
        var name = (await AuthStateProvider.GetAuthenticationStateAsync()).User.Identity?.Name;
        return string.IsNullOrEmpty(name) ? null : await UserContextFactory.CreateByUsernameAsync(name);
    }

    private string ScopeText => string.Format(T("Web_SearchResults_ScopeIn", "in {0}"),
        _request!.Describe(T("Web_Search_AllShares", "All shares")));

    private string CountText => Filter.IsActive
        ? string.Format(T("Web_SearchResults_CountFiltered", "{0} of {1} results"), _shownHits, TotalLabel)
        : string.Format(T("Web_SearchResults_Count", "{0} results"), TotalLabel);

    private string TotalLabel => SearchHitGroups.CountLabel(_allHits.Count, _capped);

    // ── Tree interaction ──

    private SearchTreeNode? FindNode(string key) => SearchResultTree.Find(_roots, key);

    private void Select(SearchTreeNode node)
    {
        _selected = node;
        _selectedKey = node.Key;
    }

    /// <summary>
    /// Coming from "n more in …": only that share stays open, and the share (or the searched
    /// folder in it) is selected and scrolled into view.
    /// </summary>
    private void ApplyFocus()
    {
        if (_request?.Focus is not { Share: { } share } focus)
            return;
        // Case-insensitive: the focus may come from a URL typed in another case than the share.
        const StringComparison ignoreCase = StringComparison.OrdinalIgnoreCase;
        foreach (var root in _roots.Where(r => !r.Share.Equals(share, ignoreCase)))
            _collapsed.Add(root.Key);
        if ((SearchResultTree.Find(_roots, SearchResultTree.KeyOf(share, focus.Path), ignoreCase)
             ?? SearchResultTree.Find(_roots, share, ignoreCase)) is { } node)
        {
            Select(node);
            _revealSelection = true;
        }
    }

    private void Toggle(SearchTreeNode node)
    {
        if (!_collapsed.Remove(node.Key))
            _collapsed.Add(node.Key);
    }

    private void ExpandAll() => _collapsed.Clear();

    // Folds every node with children, shares included: only the share rows remain.
    private void CollapseAll()
    {
        foreach (var row in SearchResultTree.Flatten(_roots, new HashSet<string>()))
            if (row.Node.Children.Count > 0)
                _collapsed.Add(row.Node.Key);
    }

    /// <summary>Double-click / Enter: a file opens in the preview, a folder expands or collapses.</summary>
    private async Task ActivateAsync(SearchTreeNode node)
    {
        Select(node);
        if (CanPreview(node))
            await PreviewAsync(node);
        else if (node.Children.Count > 0)
            Toggle(node);
    }

    private async Task OnRowKeyAsync(KeyboardEventArgs e, SearchTreeNode node)
    {
        switch (e.Key)
        {
            case "Enter":
                await ActivateAsync(node);
                break;
            case "ArrowRight" when _collapsed.Contains(node.Key):
            case "ArrowLeft" when node.Children.Count > 0 && !_collapsed.Contains(node.Key):
                Toggle(node);
                break;
            case " ":
                Select(node);
                break;
        }
    }

    private static bool CanPreview(SearchTreeNode? node) => node is { Hit: not null, IsDirectory: false };

    private string ShowInFolderLabel(SearchTreeNode? node) => node is { IsDirectory: true }
        ? T("Web_SearchResults_OpenFolder", "Open folder")
        : T("Web_SearchResults_ShowInFolder", "Show in folder");

    /// <summary>Opens the file browser: a folder is entered, a file's folder opens with the file selected.</summary>
    private void ShowInFolder(SearchTreeNode node)
    {
        CloseMenu();
        SearchHitNavigation.Open(Nav, Selection, node.Share, node.SharePath, node.Name, node.IsDirectory);
    }

    private static string T(string key, string fallback) => Resources.ResourceManager.GetString(key) ?? fallback;

    public void Dispose()
    {
        StopLiveLink();
        _cts?.Cancel();
        _cts?.Dispose();
        _cts = null;   // a search still running sees it was replaced and drops its result
        _refineCts?.Cancel();
        _refineCts?.Dispose();
        DisposePreview();
    }
}

using Kaimo_File_Server.Web.Components.Search;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Routing;
using Microsoft.JSInterop;

namespace Kaimo_File_Server.Web.Components.Pages.Search;

/// <summary>
/// Where the page's search comes from and how it stays current:
/// <list type="bullet">
/// <item>The search lives in the browser history entry. Blazor hands it over on navigation;
/// after a reload the page reads it back from <c>history.state</c>, so the results survive a
/// reload until the user leaves the page.</item>
/// <item>While the page is open, the search bar forwards new terms and scopes
/// (<see cref="SearchResultsCoordinator"/>); the page searches again in place once the user
/// pauses, keeps filters, folding and selection, and replaces the history entry so back/forward
/// and reload show the latest search.</item>
/// </list>
/// </summary>
public partial class SearchResultsPage
{
    [Inject] private SearchResultsCoordinator Coordinator { get; set; } = default!;

    // Pause after the bar's own debounce before the page searches again (~0.75 s after typing stops).
    private static readonly TimeSpan LiveDelay = TimeSpan.FromMilliseconds(500);

    private CancellationTokenSource? _askCts;

    private void StartLiveLink()
    {
        Coordinator.Asked += OnAsked;
        Nav.LocationChanged += OnLocationChanged;
    }

    private void StopLiveLink()
    {
        Coordinator.Asked -= OnAsked;
        Nav.LocationChanged -= OnLocationChanged;
        _askCts?.Cancel();
        _askCts?.Dispose();
        Coordinator.Clear();
    }

    private async Task RestoreAsync()
    {
        var state = Nav.HistoryEntryState ?? await ReadHistoryStateAsync();
        _restoring = false;
        await ShowAsync(SearchResultsRequest.Parse(state));
    }

    private async Task ShowAsync(SearchResultsRequest? request)
    {
        if (request is not null)
            Coordinator.Show(request);
        await SearchAsync(request, keepView: false);
    }

    // Back/forward between two searches stays on this page: the history entry carries the search.
    private void OnLocationChanged(object? sender, LocationChangedEventArgs e)
    {
        var path = Nav.ToBaseRelativePath(e.Location).Split('?', '#')[0].Trim('/');
        if (!path.Equals("search", StringComparison.OrdinalIgnoreCase))
            return;
        _ = InvokeAsync(async () =>
        {
            await ShowAsync(SearchResultsRequest.Parse(e.HistoryEntryState));
            StateHasChanged();
        });
    }

    // Typing in the search bar: wait until the user pauses, then search again in place.
    private void OnAsked(SearchResultsRequest request)
    {
        _askCts?.Cancel();
        var cts = _askCts = new CancellationTokenSource();
        _ = InvokeAsync(async () =>
        {
            try { await Task.Delay(LiveDelay, cts.Token); }
            catch (OperationCanceledException) { return; }

            var next = request with { Focus = null };
            Coordinator.Replace(next);
            await PersistAsync(next);
            var search = SearchAsync(next, keepView: true);
            StateHasChanged();   // shows "updating" while the old tree stays
            await search;
            StateHasChanged();
        });
    }

    private async Task<string?> ReadHistoryStateAsync()
    {
        try { return await JS.InvokeAsync<string?>("kaimoGlobalSearch.historyState"); }
        catch (JSException) { return null; }
        catch (JSDisconnectedException) { return null; }
    }

    private async Task PersistAsync(SearchResultsRequest request)
    {
        try { await JS.InvokeVoidAsync("kaimoGlobalSearch.replaceHistoryState", request.Serialize()); }
        catch (JSException) { }
        catch (JSDisconnectedException) { }
    }
}

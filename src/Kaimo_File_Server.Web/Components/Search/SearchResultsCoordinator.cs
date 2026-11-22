namespace Kaimo_File_Server.Web.Components.Search;

/// <summary>
/// Circuit-scoped link between the global search bar and the results page (/search), in the
/// spirit of <c>FileSelectionCoordinator</c>. The page announces the search it shows
/// (<see cref="Show"/>), so the bar mirrors term and scope — also after a reload, when only the
/// page can read the browser history entry. While the page is open, the bar hands new terms and
/// scopes to it (<see cref="Ask"/>) instead of listing file hits itself.
/// </summary>
public sealed class SearchResultsCoordinator
{
    /// <summary>The search on screen; null while no results page is open.</summary>
    public SearchResultsRequest? Current { get; private set; }

    /// <summary>Raised for the bar when the page shows a search (initial load, reload, back/forward).</summary>
    public event Action<SearchResultsRequest>? Shown;

    /// <summary>Raised for the page when the bar asks for another term or scope.</summary>
    public event Action<SearchResultsRequest>? Asked;

    public void Show(SearchResultsRequest request)
    {
        Current = request;
        Shown?.Invoke(request);
    }

    /// <summary>Updates the search on screen without telling the bar (it asked for it).</summary>
    public void Replace(SearchResultsRequest request) => Current = request;

    /// <returns><see langword="true"/> when an open results page took the request.</returns>
    public bool Ask(SearchResultsRequest request)
    {
        if (Asked is null)
            return false;
        Asked.Invoke(request);
        return true;
    }

    public void Clear() => Current = null;
}

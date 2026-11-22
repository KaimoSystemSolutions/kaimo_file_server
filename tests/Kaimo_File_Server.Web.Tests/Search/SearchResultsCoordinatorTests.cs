using Kaimo_File_Server.Web.Components.Search;
using Xunit;

namespace Kaimo_File_Server.Web.Tests.Search;

/// <summary>
/// The circuit-scoped link between the search bar and the results page: the page announces the
/// search it shows (so the bar mirrors term and scope, also after a reload), and the bar hands
/// new terms/scopes to the page while the user types there.
/// </summary>
public sealed class SearchResultsCoordinatorTests
{
    private static SearchResultsRequest Request(string q) => SearchResultsRequest.Create(q, [(null, "")], false, Guid.NewGuid());

    [Fact]
    public void Show_SetsCurrentAndNotifiesTheBar()
    {
        var coordinator = new SearchResultsCoordinator();
        SearchResultsRequest? seen = null;
        coordinator.Shown += r => seen = r;

        coordinator.Show(Request("abc"));

        Assert.Equal("abc", coordinator.Current!.Query);
        Assert.Equal("abc", seen!.Query);
    }

    [Fact]
    public void Ask_ReachesThePageOnly()
    {
        var coordinator = new SearchResultsCoordinator();
        var shown = 0;
        SearchResultsRequest? asked = null;
        coordinator.Shown += _ => shown++;
        coordinator.Asked += r => asked = r;

        coordinator.Ask(Request("next"));

        Assert.Equal("next", asked!.Query);
        Assert.Equal(0, shown);
    }

    [Fact]
    public void Ask_WithoutAnOpenPage_ReportsUnhandled()
        => Assert.False(new SearchResultsCoordinator().Ask(Request("x")));

    [Fact]
    public void Clear_ForgetsTheShownSearch()
    {
        var coordinator = new SearchResultsCoordinator();
        coordinator.Show(Request("abc"));

        coordinator.Clear();

        Assert.Null(coordinator.Current);
    }
}

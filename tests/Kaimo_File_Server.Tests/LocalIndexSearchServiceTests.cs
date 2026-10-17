using Kaimo_File_Server.Core.Domain;
using Kaimo_File_Server.Core.Domain.Identity;
using Kaimo_File_Server.Core.Repositories;
using Kaimo_File_Server.Core.Security;
using Kaimo_File_Server.Search;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Kaimo_File_Server.Tests;

/// <summary>
/// How the local engine talks to its index on a search: one ranked query, ACL filtering in
/// memory, excerpts only for the hits that are actually shown.
/// </summary>
public sealed class LocalIndexSearchServiceTests
{
    private readonly Mock<ISearchIndexRepository> _index = new();

    private LocalIndexSearchService Service()
    {
        var share = new ShareDefinition("Team", "/srv/team");
        var shares = new Mock<IShareRepository>();
        shares.Setup(s => s.GetByNameAsync("Team")).ReturnsAsync(share);
        var acl = new Mock<IAclService>();
        acl.Setup(a => a.HasAccessBatchAsync(
                It.IsAny<UserContext>(), share.Id,
                It.IsAny<IReadOnlyList<(string relativePath, bool isDirectory)>>(), FilePermission.ListReadData))
            .ReturnsAsync((UserContext _, Guid _, IReadOnlyList<(string relativePath, bool)> items, FilePermission _) =>
                items.ToDictionary(i => i.relativePath, i => !i.relativePath.StartsWith("secret", StringComparison.Ordinal)));
        var scopes = new ServiceCollection()
            .AddScoped(_ => shares.Object)
            .AddScoped(_ => acl.Object)
            .BuildServiceProvider()
            .GetRequiredService<IServiceScopeFactory>();

        return new LocalIndexSearchService(
            _index.Object, scopes, new SearchAclFilter(scopes, NullLogger<SearchAclFilter>.Instance),
            new DemoModeOptions(), NullLogger<LocalIndexSearchService>.Instance);
    }

    private static UserContext User()
        => new(new User(Guid.NewGuid(), "Alice", "alice", "hash", "nt"), [], [], []);

    private static FileDocument Hit(string name)
        => new() { Id = "id-" + name, FileName = name, ShareName = "Team", SharePath = name, AbsolutePath = "/srv/team/" + name, FileType = "txt" };

    private void RankedHits(params FileDocument[] hits)
        => _index.Setup(i => i.SearchAsync(
                It.IsAny<IReadOnlyList<string>>(), null, null, 0, SearchLimits.MaxRawScan, false, It.IsAny<CancellationToken>()))
            .ReturnsAsync(hits.ToList());

    [Fact]
    public async Task Search_RanksOnce_FiltersInMemory_AndFetchesExcerptsOnlyForVisibleContentHits()
    {
        // 60 unreadable hits rank first: the old paging re-ran the ranked query for every page.
        var hits = Enumerable.Range(0, 60).Select(i => Hit($"secret_{i}.txt"))
            .Concat(Enumerable.Range(0, 59).Select(i => Hit($"file_{i}.txt")))
            .Append(Hit("report.txt"))
            .ToArray();
        RankedHits(hits);
        IReadOnlyCollection<string>? excerptIds = null;
        _index.Setup(i => i.GetExcerptsAsync(It.IsAny<IReadOnlyCollection<string>>(), It.IsAny<IReadOnlyList<string>>(), It.IsAny<CancellationToken>()))
            .Callback((IReadOnlyCollection<string> ids, IReadOnlyList<string> _, CancellationToken _) => excerptIds = ids)
            .ReturnsAsync((IReadOnlyCollection<string> ids, IReadOnlyList<string> _, CancellationToken _) =>
                ids.ToDictionary(id => id, _ => "the report text"));

        var result = await Service().SearchAsync("report", User());

        Assert.Equal(hits.Skip(60).Select(h => h.FileName), result.Select(h => h.FileName));
        _index.Verify(i => i.SearchAsync(
            It.IsAny<IReadOnlyList<string>>(), It.IsAny<string?>(), It.IsAny<string?>(),
            It.IsAny<int>(), It.IsAny<int>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()), Times.Once);
        // The name hit is highlighted from its name; no excerpt is fetched for it or for denied hits.
        Assert.NotNull(excerptIds);
        Assert.Equal(hits.Skip(60).Take(59).Select(h => h.Id).Order(), excerptIds!.Order());
        Assert.Equal("the <mark>report</mark> text", result[0].HighlightSnippet);
        Assert.Equal("<mark>report</mark>.txt", result[^1].HighlightSnippet);
    }

    [Fact]
    public async Task Search_SkipsTheExcerptQuery_WhenNothingNeedsOne()
    {
        RankedHits(Hit("secret_a.txt"), Hit("report.txt"));

        var result = await Service().SearchAsync("report", User());

        Assert.Equal(["report.txt"], result.Select(h => h.FileName));
        _index.Verify(i => i.GetExcerptsAsync(
            It.IsAny<IReadOnlyCollection<string>>(), It.IsAny<IReadOnlyList<string>>(), It.IsAny<CancellationToken>()), Times.Never);
    }
}

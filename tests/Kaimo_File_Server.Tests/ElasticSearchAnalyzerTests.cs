using Elastic.Clients.Elasticsearch;
using Kaimo_File_Server.Core.Domain;
using Kaimo_File_Server.Core.Repositories;
using Kaimo_File_Server.Search;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Kaimo_File_Server.Tests;

/// <summary>
/// A fact that only runs when <c>KAIMO_TEST_ES</c> holds the URL of an Elasticsearch node, e.g.
/// <c>http://127.0.0.1:9200</c>. Analyzer behavior can only be verified against a real node.
/// </summary>
public sealed class ElasticsearchFactAttribute : FactAttribute
{
    public const string EnvironmentVariable = "KAIMO_TEST_ES";

    public ElasticsearchFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(EnvironmentVariable)))
            Skip = $"Set {EnvironmentVariable} to an Elasticsearch URL to run.";
    }
}

/// <summary>
/// The Elasticsearch mapping and query end to end on a throw-away index: queries with
/// punctuation, CJK, words longer than the n-gram span, Unicode normalization and ß, the sweep
/// of out-of-band deletions, and highlight snippets that file content cannot forge.
/// </summary>
public sealed class ElasticSearchAnalyzerTests : IAsyncLifetime
{
    private readonly string _root = Directory.CreateTempSubdirectory("kaimo-es-analyzer-").FullName;
    private readonly string _index = "kaimo-test-" + Guid.NewGuid().ToString("N");
    private ElasticsearchClient _client = null!;
    private ElasticSearchService _service = null!;

    public Task InitializeAsync()
    {
        var url = Environment.GetEnvironmentVariable(ElasticsearchFactAttribute.EnvironmentVariable);
        _client = new ElasticsearchClient(new ElasticsearchClientSettings(new Uri(url ?? "http://127.0.0.1:9200")));

        var share = new ShareDefinition("Team", _root);
        var shares = new Mock<IShareRepository>();
        shares.Setup(s => s.GetAllEnabledAsync()).ReturnsAsync(() => [share]);
        shares.Setup(s => s.GetAllAsync()).ReturnsAsync(() => [share]);
        var scopes = new ServiceCollection().AddScoped(_ => shares.Object).BuildServiceProvider()
            .GetRequiredService<IServiceScopeFactory>();

        _service = new ElasticSearchService(
            _client, NullLogger<ElasticSearchService>.Instance, scopes,
            new SearchAclFilter(scopes, NullLogger<SearchAclFilter>.Instance), _index);
        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        if (Environment.GetEnvironmentVariable(ElasticsearchFactAttribute.EnvironmentVariable) is { Length: > 0 })
            await _client.Indices.DeleteAsync(_index);
        Directory.Delete(_root, recursive: true);
    }

    private string Write(string rel, string content)
    {
        var path = Path.Combine(_root, rel);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
        return path;
    }

    private async Task<List<FileDocument>> Search(string query)
        => await _service.RawSearchAsync(query, null, null, includeRecycleBin: false, from: 0, CancellationToken.None);

    private async Task<string[]> Names(string query)
        => (await Search(query)).Select(d => d.FileName).Order().ToArray();

    [ElasticsearchFact]
    public async Task EnsureIndex_ReportsCreationOnlyOnce()
    {
        Assert.True(await _service.EnsureIndexAsync());
        Assert.False(await _service.EnsureIndexAsync());
    }

    [ElasticsearchFact]
    public async Task QueriesWithPunctuation_MatchNamesAndContent()
    {
        Write("invoice.pdf.txt", "nothing");
        Write("code.txt", "def get_user_profile_by_id(): pass\ntitle:quarterly");
        await _service.ReindexAllAsync(progress: null);

        Assert.Contains("invoice.pdf.txt", await Names("invoice.pdf"));
        Assert.Contains("code.txt", await Names("get_user_profile_by_id"));
        Assert.Contains("code.txt", await Names("title:quarterly"));
    }

    [ElasticsearchFact]
    public async Task CjkSingleCharacters_AreFound()
    {
        Write("東京の地図.txt", "日本語のテキスト");
        await _service.ReindexAllAsync(progress: null);

        Assert.Equal(["東京の地図.txt"], await Names("東"));
        Assert.Equal(["東京の地図.txt"], await Names("本"));
    }

    [ElasticsearchFact]
    public async Task WordsLongerThanTheNgramSpan_AreFound()
    {
        Write("law.txt", "Rindfleischetikettierungsueberwachungsaufgabenuebertragungsgesetz gilt");
        Write("Donaudampfschifffahrtsgesellschaft.txt", "x");
        await _service.ReindexAllAsync(progress: null);

        Assert.Equal(["law.txt"], await Names("Rindfleischetikettierungsueberwachungsaufgabenuebertragungsgesetz"));
        Assert.Equal(["law.txt"], await Names("etikettierungsueberwachung"));
        Assert.Equal(["Donaudampfschifffahrtsgesellschaft.txt"], await Names("Donaudampfschifffahrtsgesellschaft"));
    }

    [ElasticsearchFact]
    public async Task DecomposedNamesAndSharpS_MatchTheirComposedAndUppercaseForms()
    {
        Write("Kündigung.txt", "x");
        Write("address.txt", "Hauptstraße 5");
        await _service.ReindexAllAsync(progress: null);

        Assert.Equal(["Kündigung.txt"], await Names("Kündigung"));
        Assert.Equal(["address.txt"], await Names("HAUPTSTRASSE"));
        Assert.Equal(["address.txt"], await Names("hauptstraße"));
    }

    [ElasticsearchFact]
    public async Task Reindex_SweepsDocumentsOfFilesDeletedOutOfBand()
    {
        var gone = Write("gone.txt", "ephemeral");
        Write("kept.txt", "ephemeral");
        await _service.ReindexAllAsync(progress: null);
        Assert.Equal(["gone.txt", "kept.txt"], await Names("ephemeral"));

        File.Delete(gone);
        await _service.ReindexAllAsync(progress: null);

        Assert.Equal(["kept.txt"], await Names("ephemeral"));
    }

    [ElasticsearchFact]
    public async Task Highlight_CannotBeForgedByLiteralMarkTags()
    {
        Write("page.html", "a literal <mark>fake</mark> tag and the genuine word");
        await _service.ReindexAllAsync(progress: null);

        var snippet = Assert.Single(await Search("genuine")).HighlightSnippet;

        Assert.Contains("<mark>genuine</mark>", snippet);
        Assert.DoesNotContain("<mark>fake</mark>", snippet);
    }
}

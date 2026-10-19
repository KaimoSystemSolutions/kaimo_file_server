using System.Net;
using System.Text;
using Elastic.Clients.Elasticsearch;
using Elastic.Transport;
using Kaimo_File_Server.Core.Repositories;
using Kaimo_File_Server.Core.Security;
using Kaimo_File_Server.Search;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Kaimo_File_Server.Tests;

/// <summary>
/// A new Elasticsearch index (first activation, new mapping version, lost volume) is filled by a
/// full reindex. Until such a pass has completed, the build is restarted on every start, and the
/// indexes of earlier mapping versions are kept: an interrupted build (container restart, cancel,
/// outage) must neither leave the index half-filled for good nor have thrown the old one away.
/// Runs against an in-memory fake cluster, no Elasticsearch needed.
/// </summary>
public sealed class ElasticIndexBuildTests
{
    private const string Current = ElasticSearchService.DefaultIndexName;
    private const string Legacy = "kaimo-files-v2";

    private readonly FakeCluster _cluster = new();
    private string _built = string.Empty;
    private readonly Mock<ISearchConfigStore> _config = new();

    public ElasticIndexBuildTests()
    {
        _config.Setup(c => c.GetEngineAsync()).ReturnsAsync(SearchEngine.Elasticsearch);
        _config.Setup(c => c.GetElasticIndexBuiltAsync()).ReturnsAsync(() => _built);
        _config.Setup(c => c.SetElasticIndexBuiltAsync(It.IsAny<string>()))
            .Callback<string>(name => _built = name)
            .Returns(Task.CompletedTask);
    }

    [Fact]
    public async Task NewIndex_IsFilledByAFullPass_ThenMarkedBuilt_AndLegacyIndexesDropped()
    {
        _cluster.Indexes.Add(Legacy);
        var router = CreateRouter();

        await router.InitializeAsync();
        var progress = await WaitForReindexAsync(router);

        Assert.Null(progress.Error);
        Assert.Equal(Current, _built);
        Assert.Equal([Current], _cluster.Indexes);
    }

    [Fact]
    public async Task InterruptedBuild_KeepsLegacyIndexes_AndIsResumedOnTheNextStart()
    {
        _cluster.Indexes.Add(Legacy);
        var failing = CreateRouter(sharesFail: true);

        await failing.InitializeAsync();
        Assert.NotNull((await WaitForReindexAsync(failing)).Error);
        Assert.Equal(string.Empty, _built);
        Assert.Contains(Legacy, _cluster.Indexes);

        // Next process start: the index exists now, but its build never completed.
        var restarted = CreateRouter();
        await restarted.InitializeAsync();
        var progress = await WaitForReindexAsync(restarted);

        Assert.Null(progress.Error);
        Assert.Equal(Current, _built);
        Assert.DoesNotContain(Legacy, _cluster.Indexes);
    }

    [Fact]
    public async Task CompletedBuild_StartsNoReindex()
    {
        _cluster.Indexes.Add(Current);
        _built = Current;
        var router = CreateRouter();

        await router.InitializeAsync();

        Assert.Null(router.GetReindexProgress().StartedAt);
        Assert.DoesNotContain(_cluster.Requests, r => r.StartsWith("DELETE"));
    }

    [Fact]
    public async Task IndexLostAfterItsBuild_IsRebuilt()
    {
        // The flag says built, but the index is gone (Elasticsearch volume wiped).
        _built = Current;
        var router = CreateRouter();

        await router.InitializeAsync();

        Assert.NotNull(router.GetReindexProgress().StartedAt);
        _config.Verify(c => c.SetElasticIndexBuiltAsync(string.Empty), Times.Once);
        await WaitForReindexAsync(router);
        Assert.Equal(Current, _built);
    }

    [Fact]
    public async Task ShareScopedPass_DoesNotMarkTheIndexBuilt()
    {
        _cluster.Indexes.Add(Current);
        var router = CreateRouter();

        Assert.True(await router.TryStartReindexAsync("docs"));
        await WaitForReindexAsync(router);

        Assert.Equal(string.Empty, _built);
    }

    private static async Task<ReindexProgress> WaitForReindexAsync(SearchServiceRouter router)
    {
        for (int i = 0; i < 500; i++)
        {
            var progress = router.GetReindexProgress();
            if (progress.FinishedAt is not null)
                return progress;
            await Task.Delay(10);
        }
        throw new TimeoutException("The reindex did not finish.");
    }

    /// <param name="sharesFail">Reading the shares fails, so the pass ends with an error (interrupted).</param>
    private SearchServiceRouter CreateRouter(bool sharesFail = false)
    {
        var shares = new Mock<IShareRepository>();
        if (sharesFail)
            shares.Setup(r => r.GetAllEnabledAsync()).ThrowsAsync(new IOException("database gone"));
        else
            shares.Setup(r => r.GetAllEnabledAsync()).ReturnsAsync([]);

        var scopeFactory = new ServiceCollection()
            .AddSingleton(shares.Object)
            .BuildServiceProvider()
            .GetRequiredService<IServiceScopeFactory>();
        var acl = new SearchAclFilter(scopeFactory, NullLogger<SearchAclFilter>.Instance);
        var client = new ElasticsearchClient(new ElasticsearchClientSettings(
            new SingleNodePool(new Uri("http://localhost:9200")),
            new HttpRequestInvoker((_, _) => _cluster)));

        return new SearchServiceRouter(
            new ElasticSearchService(client, NullLogger<ElasticSearchService>.Instance, scopeFactory, acl),
            new LocalIndexSearchService(
                Mock.Of<ISearchIndexRepository>(), scopeFactory, acl, new DemoModeOptions(),
                NullLogger<LocalIndexSearchService>.Instance),
            new FilenameSearchService(scopeFactory, acl, NullLogger<FilenameSearchService>.Instance),
            client,
            _config.Object,
            NullLogger<SearchServiceRouter>.Instance);
    }

    /// <summary>Answers ping, health and index create/exists/delete; remembers which indexes exist.</summary>
    private sealed class FakeCluster : HttpMessageHandler
    {
        public HashSet<string> Indexes { get; } = new();
        public List<string> Requests { get; } = new();

        private const string Health = """
            {"cluster_name":"test","status":"green","timed_out":false,"number_of_nodes":1,
             "number_of_data_nodes":1,"active_primary_shards":0,"active_shards":0,"relocating_shards":0,
             "initializing_shards":0,"unassigned_shards":0,"unassigned_primary_shards":0,
             "delayed_unassigned_shards":0,"number_of_pending_tasks":0,"number_of_in_flight_fetch":0,
             "task_max_waiting_in_queue_millis":0,"active_shards_percent_as_number":100.0}
            """;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            string path = request.RequestUri!.AbsolutePath.Trim('/');
            string method = request.Method.Method;
            (HttpStatusCode status, string body) answer;
            lock (Indexes)
            {
                Requests.Add($"{method} /{path}");
                answer = (method, path) switch
                {
                    ("HEAD", "") => (HttpStatusCode.OK, ""),
                    ("GET", "_cluster/health") => (HttpStatusCode.OK, Health),
                    ("HEAD", _) => (Indexes.Contains(path) ? HttpStatusCode.OK : HttpStatusCode.NotFound, ""),
                    ("PUT", _) when Indexes.Add(path) =>
                        (HttpStatusCode.OK, $$"""{"acknowledged":true,"shards_acknowledged":true,"index":"{{path}}"}"""),
                    ("DELETE", _) when Indexes.Remove(path) => (HttpStatusCode.OK, """{"acknowledged":true}"""),
                    _ => (HttpStatusCode.NotFound, """{"error":"not found","status":404}""")
                };
            }

            var response = new HttpResponseMessage(answer.status)
            {
                Content = new StringContent(answer.body, Encoding.UTF8, "application/json"),
                RequestMessage = request
            };
            response.Headers.Add("x-elastic-product", "Elasticsearch");
            return Task.FromResult(response);
        }
    }
}

using System.Text;
using System.Text.Json;
using Elastic.Clients.Elasticsearch;
using Elastic.Transport;
using Kaimo_File_Server.Core.Domain;
using Kaimo_File_Server.Core.Repositories;
using Kaimo_File_Server.Search;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;
using static Kaimo_File_Server.Tests.ContentProviderHardeningTests;

namespace Kaimo_File_Server.Tests;

/// <summary>
/// Elasticsearch indexing must skip a file whose indexed size and last-write time still match,
/// without reading or extracting it — like the local index does — so a repeated reindex of a
/// large share stays cheap. Runs against a canned in-memory transport, no cluster needed.
/// </summary>
public sealed class ElasticSearchUnchangedSkipTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("kaimo-es-skip-").FullName;
    private readonly string _file;
    private readonly List<string> _requests = new();
    private readonly ListLogger _log = new();

    public ElasticSearchUnchangedSkipTests()
    {
        _file = Path.Combine(_dir, "notes.txt");
        File.WriteAllText(_file, "hello index");
    }

    public void Dispose()
    {
        Directory.Delete(_dir, recursive: true);
        Assert.Empty(_log.Problems);
    }

    [Fact]
    public async Task UnchangedFile_IsNeitherReadNorWritten()
    {
        var service = CreateService(IndexedDoc(File.GetLastWriteTimeUtc(_file).Ticks));
        var stream = new CountingStream(File.ReadAllBytes(_file));

        await service.onFileCreated(_file, Task.FromResult<Stream>(stream));

        Assert.Equal(0, stream.BytesRead);
        Assert.DoesNotContain(_requests, IsWrite);
    }

    [Fact]
    public async Task ChangedTimestamp_IsExtractedAndWritten()
    {
        var service = CreateService(IndexedDoc(File.GetLastWriteTimeUtc(_file).Ticks - 1));
        var stream = new CountingStream(File.ReadAllBytes(_file));

        await service.onFileCreated(_file, Task.FromResult<Stream>(stream));

        Assert.True(stream.BytesRead > 0);
        Assert.Contains(_requests, IsWrite);
    }

    [Fact]
    public async Task DocumentIndexedBeforeTimestamps_IsRewrittenOnce()
    {
        var service = CreateService(IndexedDoc(fileModifiedTicks: null));

        await service.onFileCreated(_file, Task.FromResult<Stream>(new CountingStream(File.ReadAllBytes(_file))));

        Assert.Contains(_requests, IsWrite);
    }

    [Fact]
    public void RenamedDocument_KeepsTheStamp_SoTheNextReindexSkipsIt()
    {
        // A rename does not change the file's content or last-write time.
        var source = new FileDocument
        {
            Id = "x", FileName = "notes.txt", ShareName = "docs", AbsolutePath = _file,
            SharePath = "notes.txt", FileType = "txt", FileSizeBytes = 11, FileModifiedTicks = 638_000_000_000_000_000
        };

        var renamed = ElasticSearchService.BuildRenamedDocument(
            source, Path.Combine(_dir, "renamed.txt"), new ShareDefinition("docs", _dir));

        Assert.Equal(source.FileModifiedTicks, renamed.FileModifiedTicks);
        Assert.Equal(source.FileSizeBytes, renamed.FileSizeBytes);
    }

    /// <summary>onFileCreated logs and swallows failures, so every test also asserts none occurred.</summary>
    private sealed class ListLogger : ILogger<ElasticSearchService>
    {
        public List<string> Problems { get; } = new();
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (logLevel >= LogLevel.Warning)
                Problems.Add($"{logLevel}: {formatter(state, exception)} {exception?.GetType().Name}");
        }
    }

    private static bool IsWrite(string request) => request.StartsWith("PUT") || request.StartsWith("POST");

    /// <summary>A service whose cluster answers every request with <paramref name="response"/>.</summary>
    private ElasticSearchService CreateService(string response)
    {
        var settings = new ElasticsearchClientSettings(
                new SingleNodePool(new Uri("http://localhost:9200")),
                new InMemoryRequestInvoker(Encoding.UTF8.GetBytes(response), headers: new()
                {
                    // The client refuses responses that do not identify as Elasticsearch.
                    ["x-elastic-product"] = ["Elasticsearch"]
                }))
            .OnRequestCompleted(call => _requests.Add($"{call.HttpMethod} {call.Uri?.AbsolutePath}"));

        var shares = new Mock<IShareRepository>();
        shares.Setup(r => r.GetAllAsync()).ReturnsAsync(
            [new ShareDefinition("docs", _dir)]);
        var scopeFactory = new ServiceCollection()
            .AddSingleton(shares.Object)
            .BuildServiceProvider()
            .GetRequiredService<IServiceScopeFactory>();

        return new ElasticSearchService(
            new ElasticsearchClient(settings),
            _log,
            scopeFactory,
            new SearchAclFilter(scopeFactory, NullLogger<SearchAclFilter>.Instance));
    }

    /// <summary>A GET response holding <see cref="_file"/> as currently indexed.</summary>
    private string IndexedDoc(long? fileModifiedTicks) => JsonSerializer.Serialize(new
    {
        _index = "kaimo-files-v2",
        _id = "x",
        _version = 1,
        _seq_no = 0,
        _primary_term = 1,
        found = true,
        _source = new
        {
            id = "x",
            fileName = "notes.txt",
            shareName = "docs",
            absolutePath = _file,
            sharePath = "notes.txt",
            content = "hello index",
            fileType = "txt",
            fileSizeBytes = new FileInfo(_file).Length,
            fileModifiedTicks
        }
    });
}

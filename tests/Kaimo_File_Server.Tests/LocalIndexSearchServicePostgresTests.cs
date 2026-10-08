using Kaimo_File_Server.Core.Domain;
using Kaimo_File_Server.Core.Repositories;
using Kaimo_File_Server.Core.Security;
using Kaimo_File_Server.Infrastructure.Repositories;
using Kaimo_File_Server.Search;
using Kaimo_File_Server.Tests.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Kaimo_File_Server.Tests;

/// <summary>
/// The local index engine end to end over real files and real PostgreSQL: reindex walk, hidden
/// folders, change detection, the out-of-band sweep, SMB name-only creates and the demo guard.
/// Skipped unless <c>KAIMO_TEST_PG</c> is set.
/// </summary>
public sealed class LocalIndexSearchServicePostgresTests : PostgresTestBase, IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "kaimo-local-index-" + Guid.NewGuid().ToString("N"));
    private readonly SearchIndexRepository _repo;

    public LocalIndexSearchServicePostgresTests()
    {
        Directory.CreateDirectory(_root);
        _repo = new SearchIndexRepository(DbFactory);
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private LocalIndexSearchService Service(bool demo = false)
    {
        var share = new ShareDefinition("Team", _root);
        var shares = new Mock<IShareRepository>();
        shares.Setup(s => s.GetAllEnabledAsync()).ReturnsAsync(() => [share]);
        shares.Setup(s => s.GetAllAsync()).ReturnsAsync(() => [share]);
        var scopes = new ServiceCollection()
            .AddScoped(_ => shares.Object)
            .BuildServiceProvider()
            .GetRequiredService<IServiceScopeFactory>();

        return new LocalIndexSearchService(
            _repo, scopes, new SearchAclFilter(scopes, NullLogger<SearchAclFilter>.Instance),
            new DemoModeOptions { ReadOnly = demo }, NullLogger<LocalIndexSearchService>.Instance);
    }

    private string Write(string rel, string content)
    {
        var path = Path.Combine(_root, rel);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
        return path;
    }

    private async Task<string[]> Names(string term)
        => (await _repo.SearchAsync(new[] { term }, null, null, 0, 50))
            .Select(d => d.FileName).Order().ToArray();

    [PostgresFact]
    public async Task Reindex_IndexesContentAndFolders_SkipsHidden_AndSweepsOutOfBandChanges()
    {
        var a = Write("a.txt", "hello alpha");
        var b = Write("sub/b.txt", "beta");
        Write(".hidden/c.txt", "alpha");
        var service = Service();

        await service.ReindexAllAsync(progress: null);

        Assert.Equal(new[] { "a.txt" }, await Names("alpha"));
        Assert.Equal(new[] { "sub" }, await Names("sub"));

        // Changed directly on the volume, bypassing the change log.
        File.Delete(a);
        File.WriteAllText(b, "gamma ray");
        File.SetLastWriteTimeUtc(b, DateTime.UtcNow.AddMinutes(1));
        await service.ReindexAllAsync(progress: null);

        Assert.Empty(await Names("alpha"));
        Assert.Equal(new[] { "b.txt" }, await Names("gamma"));
    }

    [PostgresFact]
    public async Task NameOnlyCreate_FromLockedSmbFile_IsReExtractedOnTheNextWrite()
    {
        var path = Write("x.txt", "delta");
        var service = Service();

        await service.onFileCreated(path, Task.FromResult(Stream.Null));
        Assert.Empty(await Names("delta"));
        Assert.Equal(new[] { "x.txt" }, await Names("x.txt"));

        // Same size and timestamp as before — must still be read, the first pass had no content.
        await service.onFileCreated(path, Task.FromResult<Stream>(File.OpenRead(path)));
        Assert.Equal(new[] { "x.txt" }, await Names("delta"));
    }

    [PostgresFact]
    public async Task ReadOnlyDemo_WritesNothing()
    {
        var path = Write("x.txt", "delta");
        var service = Service(demo: true);

        await service.onFileCreated(path, Task.FromResult<Stream>(File.OpenRead(path)));
        await service.ReindexAllAsync(progress: null);

        Assert.Equal(0, (await _repo.GetStatsAsync()).Documents);
    }
}

using System.IO.Compression;
using Kaimo_File_Server.Core.Domain;
using Kaimo_File_Server.Core.Repositories;
using Kaimo_File_Server.Infrastructure.Storage;
using Kaimo_File_Server.Search;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using Xunit;

namespace Kaimo_File_Server.Tests;

/// <summary>
/// A fact that only runs where the current user may create symbolic links (always on Linux;
/// on Windows only with Developer Mode or elevation).
/// </summary>
public sealed class SymlinkFactAttribute : FactAttribute
{
    public SymlinkFactAttribute()
    {
        var probe = Path.Combine(Path.GetTempPath(), "kaimo-link-probe-" + Guid.NewGuid().ToString("N"));
        try { File.CreateSymbolicLink(probe, Path.GetTempPath()); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Skip = "Creating symbolic links is not permitted for this user.";
        }
        finally { try { File.Delete(probe); } catch { /* not created */ } }
    }
}

/// <summary>
/// Symbolic links inside a share (only creatable directly on the volume) are never followed:
/// one pointing outside the share must not expose foreign files (/etc/passwd) through a
/// transport, an archive or the search index, and a loop (x -> .) must not multiply entries.
/// </summary>
public sealed class SymlinkContainmentTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("kaimo-links-").FullName;
    private readonly string _outside = Directory.CreateTempSubdirectory("kaimo-outside-").FullName;
    private readonly FileSystemStorage _storage;

    public SymlinkContainmentTests()
    {
        File.WriteAllText(Path.Combine(_outside, "passwd"), "root:x:0:0");
        Directory.CreateDirectory(Path.Combine(_root, "docs"));
        File.WriteAllText(Path.Combine(_root, "docs", "real.txt"), "real");
        _storage = new FileSystemStorage(_root, Guid.NewGuid(), serviceProvider: null);
    }

    public void Dispose()
    {
        // Delete links first so the recursive delete never walks into their targets.
        foreach (var link in new[] { "leak.txt", "loop", "docs/out" })
        {
            var path = Path.Combine(_root, link);
            try { if (new FileInfo(path).LinkTarget is not null) File.Delete(path); } catch { }
            try { if (new DirectoryInfo(path).LinkTarget is not null) Directory.Delete(path); } catch { }
        }
        Directory.Delete(_root, recursive: true);
        Directory.Delete(_outside, recursive: true);
    }

    private void CreateLinks()
    {
        File.CreateSymbolicLink(Path.Combine(_root, "leak.txt"), Path.Combine(_outside, "passwd"));
        Directory.CreateSymbolicLink(Path.Combine(_root, "loop"), _root);
        Directory.CreateSymbolicLink(Path.Combine(_root, "docs", "out"), _outside);
    }

    [SymlinkFact]
    public async Task Reindex_SkipsLinkedFilesAndDirectories()
    {
        CreateLinks();
        var share = new ShareDefinition("Team", _root);
        var shares = new Mock<IShareRepository>();
        shares.Setup(s => s.GetAllEnabledAsync()).ReturnsAsync(() => [share]);
        var scopes = new ServiceCollection().AddScoped(_ => shares.Object).BuildServiceProvider()
            .GetRequiredService<IServiceScopeFactory>();

        var targets = await SearchDocuments.CollectReindexTargetsAsync(scopes, shareName: null);

        Assert.Equal([Path.Combine(_root, "docs", "real.txt")], targets.Files.Select(f => f.Path));
        Assert.Equal([Path.Combine(_root, "docs")], targets.Directories.Select(d => d.Path));
    }

    [SymlinkFact]
    public async Task Storage_TreatsAnythingReachedThroughALinkAsMissing()
    {
        CreateLinks();

        await Assert.ThrowsAsync<FileNotFoundException>(() => _storage.ReadAsync("leak.txt"));
        await Assert.ThrowsAsync<FileNotFoundException>(() => _storage.ReadAsync("docs/out/passwd"));
        await Assert.ThrowsAsync<FileNotFoundException>(() => _storage.ReadAsync("loop/docs/real.txt"));
        await Assert.ThrowsAsync<FileNotFoundException>(() => _storage.GetMetadataAsync("leak.txt"));
        await Assert.ThrowsAsync<FileNotFoundException>(
            () => _storage.WriteAsync("docs/out/planted.txt", new MemoryStream([1])));
        Assert.False(File.Exists(Path.Combine(_outside, "planted.txt")));

        // The real file is untouched by the check.
        await using var real = await _storage.ReadAsync("docs/real.txt");
        Assert.Equal(4, real.Length);
    }

    [SymlinkFact]
    public async Task Storage_ListingHidesLinks()
    {
        CreateLinks();

        Assert.Equal(["docs"], (await _storage.ListAsync("")).Select(e => e.Name));
        Assert.Equal(["real.txt"], (await _storage.ListAsync("docs")).Select(e => e.Name));
    }

    [SymlinkFact]
    public async Task Archive_AndDirectorySize_DoNotFollowLinks()
    {
        CreateLinks();
        Directory.CreateSymbolicLink(Path.Combine(_root, "docs", "loop"), _root);

        await _storage.ArchiveAsync(["docs"], "docs.zip", ".zip");
        using (var zip = ZipFile.OpenRead(Path.Combine(_root, "docs.zip")))
            Assert.Equal(["docs/real.txt"], zip.Entries.Select(e => e.FullName));

        Assert.Equal(4, await _storage.GetDirectorySizeAsync("docs"));
        Directory.Delete(Path.Combine(_root, "docs", "loop"));
    }
}

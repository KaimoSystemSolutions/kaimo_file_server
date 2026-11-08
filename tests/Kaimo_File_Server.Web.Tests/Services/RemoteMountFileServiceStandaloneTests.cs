using System.Text;
using Kaimo_File_Server.Core.Domain;
using Xunit;

namespace Kaimo_File_Server.Web.Tests.Services;

/// <summary>The adapter over a standalone virtual share: an empty mount path, so paths are the share's own.</summary>
public sealed class RemoteMountFileServiceStandaloneTests : IDisposable
{
    private readonly MountTestHarness _h = new();

    public RemoteMountFileServiceStandaloneTests()
    {
        _h.Remote.Directories.Add("root/docs");
        _h.Remote.Files["root/a.txt"] = "hello"u8.ToArray();
        _h.Remote.Files["root/docs/b.txt"] = "12345"u8.ToArray();
    }

    public void Dispose() => _h.Dispose();

    [Fact]
    public async Task List_OfTheRoot_ReturnsShareRelativePaths()
    {
        var items = await _h.NewStandaloneAdapter().ListAsync(string.Empty, _h.User);
        Assert.Equal(["a.txt", "docs"], items.Select(i => i.Path).Order());
    }

    [Fact]
    public async Task List_OfASubfolder_ReturnsShareRelativePaths()
        => Assert.Equal("docs/b.txt",
            Assert.Single(await _h.NewStandaloneAdapter().ListAsync("docs", _h.User)).Path);

    [Fact]
    public async Task GetMetadata_KeepsTheWholeName()
    {
        var meta = await _h.NewStandaloneAdapter().GetMetadataAsync("a.txt", _h.User);
        Assert.Equal("a.txt", meta.Name);
        Assert.Equal("a.txt", meta.Path);
        Assert.Equal(5, meta.Size);
    }

    [Fact]
    public async Task GetMetadata_OfTheRoot_IsADirectory()
    {
        var meta = await _h.NewStandaloneAdapter().GetMetadataAsync(string.Empty, _h.User);
        Assert.True(meta.IsDirectory);
        Assert.Equal(string.Empty, meta.Path);
    }

    [Fact]
    public async Task Read_ReachesTheProviderFile()
    {
        await using var stream = await _h.NewStandaloneAdapter().ReadFileAsync("docs/b.txt", _h.User);
        using var reader = new StreamReader(stream);
        Assert.Equal("12345", await reader.ReadToEndAsync());
    }

    [Fact]
    public async Task Write_LandsBelowTheRemoteRoot()
    {
        await _h.NewStandaloneAdapter().WriteFileAsync(
            "new.txt", new MemoryStream(Encoding.UTF8.GetBytes("x")), _h.User, CancellationToken.None);
        Assert.Equal("x"u8.ToArray(), _h.Remote.Files["root/new.txt"]);
    }

    [Theory]
    [InlineData("../secret")]
    [InlineData("docs/../../secret")]
    public async Task Traversal_IsRefused(string path)
        => await Assert.ThrowsAsync<UnauthorizedAccessException>(() => _h.NewStandaloneAdapter().ListAsync(path, _h.User));

    [Fact]
    public async Task TheRoot_CannotBeDeletedOrRenamed()
    {
        var adapter = _h.NewStandaloneAdapter();
        Assert.False(await adapter.CanDeleteAsync(string.Empty, _h.User));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => adapter.DeleteFileAsync(string.Empty, _h.User, false));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => adapter.RenameAsync(string.Empty, "x", _h.User));
        Assert.True(_h.Remote.Files.ContainsKey("root/a.txt"));
    }

    [Fact]
    public async Task ReadOnlyPermission_RefusesWrites()
    {
        var adapter = _h.NewStandaloneAdapter(CloudAccessPermission.Read);
        Assert.False(await adapter.CanWriteAsync("a.txt", _h.User));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => adapter.DeleteFileAsync("a.txt", _h.User, false));
    }

    [Fact]
    public async Task Exists_KnowsTheRootAndMissingItems()
    {
        var adapter = _h.NewStandaloneAdapter();
        Assert.True(await adapter.ExistsAsync(string.Empty, _h.User));
        Assert.True(await adapter.ExistsAsync("docs", _h.User));
        Assert.False(await adapter.ExistsAsync("nope", _h.User));
    }
}

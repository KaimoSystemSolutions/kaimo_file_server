using Xunit;

namespace Kaimo_File_Server.Tests;

/// <summary>
/// Regression tests for share-root boundary enforcement in
/// <c>FileSystemStorage.ToAbsolutePath</c>.
///
/// The headline case is the SIBLING-PREFIX escape: a share rooted at
/// "<parent>/share" must never be able to reach "<parent>/share-secret",
/// even though the latter's absolute path starts with the former's string.
/// The previous <c>StartsWith</c> check (without a trailing separator)
/// allowed exactly this.
/// </summary>
public class FileSystemStoragePathTraversalTests : IDisposable
{
    private readonly string _parent;
    private readonly string _shareRoot;
    private readonly string _siblingSecret;
    private readonly FileSystemStorageTestable _sut;

    public FileSystemStoragePathTraversalTests()
    {
        _parent = Path.Combine(Path.GetTempPath(),
            "kaimo_boundary_" + Guid.NewGuid().ToString("N"));

        // Two siblings whose names share a prefix: "share" vs "share-secret".
        _shareRoot = Path.Combine(_parent, "share");
        _siblingSecret = Path.Combine(_parent, "share-secret");

        Directory.CreateDirectory(_shareRoot);
        Directory.CreateDirectory(_siblingSecret);
        File.WriteAllText(Path.Combine(_siblingSecret, "secret.txt"), "TOP SECRET");

        _sut = new FileSystemStorageTestable(_shareRoot);
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_parent)) Directory.Delete(_parent, true); } catch { }
    }

    [Fact]
    public async Task ReadAsync_SiblingPrefixEscape_ThrowsUnauthorized()
    {
        // "../share-secret/secret.txt" resolves to the sibling directory whose
        // name starts with the root name. Must be rejected.
        await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => _sut.ReadAsync("../share-secret/secret.txt"));
    }

    [Fact]
    public async Task WriteAsync_SiblingPrefixEscape_ThrowsUnauthorized()
    {
        await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => _sut.WriteAsync("../share-secret/planted.txt", new MemoryStream([1])));

        Assert.False(File.Exists(Path.Combine(_siblingSecret, "planted.txt")),
            "Write escaped the share root into a sibling directory.");
    }

    [Fact]
    public async Task GetMetadataAsync_SiblingPrefixEscape_ThrowsUnauthorized()
    {
        await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => _sut.GetMetadataAsync("../share-secret/secret.txt"));
    }

    [Fact]
    public async Task DeleteAsync_SiblingPrefixEscape_ThrowsUnauthorized()
    {
        await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => _sut.DeleteAsync("../share-secret/secret.txt"));

        Assert.True(File.Exists(Path.Combine(_siblingSecret, "secret.txt")),
            "Delete escaped the share root and removed a sibling's file.");
    }

    // ─────────────── Sanity: legitimate access still works ───────────────

    [Fact]
    public async Task WriteAndRead_WithinRoot_StillWorks()
    {
        await _sut.WriteAsync("sub/ok.txt", new MemoryStream([7, 8, 9]));

        using var stream = await _sut.ReadAsync("sub/ok.txt");
        using var ms = new MemoryStream();
        await stream.CopyToAsync(ms);

        Assert.Equal([7, 8, 9], ms.ToArray());
        Assert.True(File.Exists(Path.Combine(_shareRoot, "sub", "ok.txt")));
    }
}

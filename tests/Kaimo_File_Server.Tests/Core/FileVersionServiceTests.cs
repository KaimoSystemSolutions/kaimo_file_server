using Kaimo_File_Server.Core.Domain;
using Kaimo_File_Server.Core.Repositories;
using Kaimo_File_Server.Core.Services.File;
using System.IO.Compression;
using System.Security.Cryptography;
using Moq;
using Xunit;

namespace Kaimo_File_Server.Tests;

public partial class FileVersionServiceTests : IDisposable
{
    // Version history is scoped per share. Most tests operate within a single
    // share; the cross-share isolation tests use a second one.
    private static readonly Guid ShareA = Guid.NewGuid();
    private static readonly Guid ShareB = Guid.NewGuid();

    private readonly string _testRoot;
    private readonly string _versionRoot;
    private readonly MockFileVersionRepository _repo;
    private readonly MutableTimeProvider _time = new();
    private readonly FileVersionService _sut;

    public FileVersionServiceTests()
    {
        _testRoot = Path.Combine(Path.GetTempPath(),
            "kaimo_version_test_" + Guid.NewGuid().ToString("N"));
        _versionRoot = Path.Combine(_testRoot, "versions");
        Directory.CreateDirectory(_testRoot);

        _repo = new MockFileVersionRepository();
        _sut = new FileVersionService(_repo, _versionRoot,
            defaultMaxVersions: 5, defaultMaxAge: null, timeProvider: _time,
            diskSpaceProbe: _ => (Free: 1L << 50, Total: 1L << 50));
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_testRoot)) Directory.Delete(_testRoot, true); } catch { }
    }

    // -- Helper --

    private static Stream ToStream(string content)
        => new MemoryStream(System.Text.Encoding.UTF8.GetBytes(content));

    private static Stream ToStream(byte[] data)
        => new MemoryStream(data);

    private static string ComputeHash(byte[] data)
    {
        using var sha = SHA256.Create();
        return Convert.ToHexString(sha.ComputeHash(data));
    }

    private string BlobPathFor(byte[] data)
    {
        var hash = ComputeHash(data);
        return Path.Combine(
            _versionRoot, hash[..2], hash[2..4], hash + ".bin.gz");
    }

    // ═══════════════════════════════════════════
    //  Version Creation
    // ═══════════════════════════════════════════

    [Fact]
    public async Task CreateVersionAsync_FirstVersion_CreatesVersionAndBlob()
    {
        var version = await _sut.CreateVersionAsync(ShareA, "doc.txt", ToStream("Hello World"), "user1");

        Assert.NotNull(version);
        Assert.Equal(ShareA, version.ShareId);
        Assert.Equal("doc.txt", version.FilePath);
        Assert.Equal(1, version.VersionNumber);
        Assert.Equal("user1", version.CreatedBy);
        Assert.True(version.Size > 0);
        Assert.NotEmpty(version.ContentHash);
        Assert.NotEmpty(version.StoragePath);

        // Blob file should exist (compressed)
        var blobPath = Path.Combine(_versionRoot, version.StoragePath);
        Assert.True(File.Exists(blobPath));
    }

    [Fact]
    public async Task CreateVersionAsync_SecondVersion_IncrementsVersionNumber()
    {
        await _sut.CreateVersionAsync(ShareA, "doc.txt", ToStream("Version 1"));
        var v2 = await _sut.CreateVersionAsync(ShareA, "doc.txt", ToStream("Version 2"));

        Assert.NotNull(v2);
        Assert.Equal(2, v2.VersionNumber);
    }

    [Fact]
    public async Task CreateVersionAsync_UnchangedContent_ReturnsNull()
    {
        var content = "Same content";
        await _sut.CreateVersionAsync(ShareA, "doc.txt", ToStream(content));

        // Same content again
        var v2 = await _sut.CreateVersionAsync(ShareA, "doc.txt", ToStream(content));

        Assert.Null(v2); // Dedup: no new version
    }

    [Fact]
    public async Task CreateVersionAsync_ChangedContent_ReturnsNewVersion()
    {
        await _sut.CreateVersionAsync(ShareA, "doc.txt", ToStream("Version A"));
        var v2 = await _sut.CreateVersionAsync(ShareA, "doc.txt", ToStream("Version B"));

        Assert.NotNull(v2);
        Assert.Equal(2, v2.VersionNumber);
    }

    [Fact]
    public async Task CreateVersionAsync_DifferentFiles_IndependentVersionNumbers()
    {
        var v1 = await _sut.CreateVersionAsync(ShareA, "a.txt", ToStream("Alpha"));
        var v2 = await _sut.CreateVersionAsync(ShareA, "b.txt", ToStream("Beta"));

        Assert.Equal(1, v1!.VersionNumber);
        Assert.Equal(1, v2!.VersionNumber);
    }

    [Fact]
    public async Task CreateVersionAsync_SameContentDifferentFiles_SharesBlob()
    {
        var content = "Shared content across files";
        var v1 = await _sut.CreateVersionAsync(ShareA, "a.txt", ToStream(content));
        var v2 = await _sut.CreateVersionAsync(ShareA, "b.txt", ToStream(content));

        Assert.NotNull(v1);
        Assert.NotNull(v2);

        // Same storage path = same blob (CAS dedup)
        Assert.Equal(v1.StoragePath, v2.StoragePath);

        // Only one blob file on disk
        var blobPath = Path.Combine(_versionRoot, v1.StoragePath);
        Assert.True(File.Exists(blobPath));
    }

    [Fact]
    public async Task CreateVersionAsync_BlobIsGzipCompressed()
    {
        var data = new string('A', 10000); // highly compressible
        var version = await _sut.CreateVersionAsync(ShareA, "big.txt", ToStream(data));

        Assert.NotNull(version);

        var blobPath = Path.Combine(_versionRoot, version.StoragePath);
        var compressedSize = new FileInfo(blobPath).Length;

        // Compressed should be significantly smaller than original
        Assert.True(compressedSize < version.Size,
            $"Compressed {compressedSize} should be < original {version.Size}");
    }

    [Fact]
    public async Task CreateVersionAsync_TimestampTruncatedToSeconds()
    {
        var version = await _sut.CreateVersionAsync(ShareA, "doc.txt", ToStream("data"));

        Assert.NotNull(version);
        Assert.Equal(0, version.SnapshotTimestampUtc.Millisecond);
        Assert.Equal(DateTimeKind.Utc, version.SnapshotTimestampUtc.Kind);
    }

    [Fact]
    public async Task CreateVersionAsync_EmptyStream_CreatesVersion()
    {
        // Even empty files get versioned (if first version)
        var version = await _sut.CreateVersionAsync(ShareA, "empty.txt", new MemoryStream(Array.Empty<byte>()));

        // Empty stream has size 0, but hash is still computed
        // Whether this creates a version depends on implementation
        // An empty file still has a valid SHA-256 hash
        Assert.NotNull(version);
        Assert.Equal(0, version.Size);
    }

    [Fact]
    public async Task CreateVersionAsync_DbInsertFailure_RemovesNewBlob()
    {
        var repo = new Mock<IFileVersionRepository>();
        repo.Setup(r => r.ExistsWithHashAsync(It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<string>()))
            .ReturnsAsync(false);
        repo.Setup(r => r.GetMaxVersionNumberAsync(It.IsAny<Guid>(), It.IsAny<string>()))
            .ReturnsAsync(0);
        repo.Setup(r => r.GetVersionAsync(It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<DateTime>()))
            .ReturnsAsync((FileVersion?)null);
        repo.Setup(r => r.CreateAsync(It.IsAny<FileVersion>()))
            .ThrowsAsync(new IOException("database unavailable"));
        repo.Setup(r => r.IsStoragePathReferencedAsync(It.IsAny<string>()))
            .ReturnsAsync(false);
        var root = Path.Combine(_versionRoot, "failed-insert");
        var service = new FileVersionService(repo.Object, root, timeProvider: _time);

        await Assert.ThrowsAsync<IOException>(() =>
            service.CreateVersionAsync(ShareA, "doc.txt", ToStream("orphan candidate")));

        Assert.Empty(Directory.GetFiles(root, "*.bin.gz", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task CreateVersionAsync_CorruptExistingBlob_IsReplacedBeforeUse()
    {
        var data = System.Text.Encoding.UTF8.GetBytes("repair this blob");
        var blobPath = BlobPathFor(data);
        Directory.CreateDirectory(Path.GetDirectoryName(blobPath)!);
        await File.WriteAllBytesAsync(blobPath, [1, 2, 3, 4]);

        var version = await _sut.CreateVersionAsync(
            ShareA, "repair.txt", ToStream(data));
        await using var restored = await _sut.ReadVersionAsync(
            ShareA, "repair.txt", version!.SnapshotTimestampUtc);
        using var copy = new MemoryStream();
        await restored.CopyToAsync(copy);

        Assert.Equal(data, copy.ToArray());
        Assert.Empty(Directory.GetFiles(
            Path.GetDirectoryName(blobPath)!, "*.tmp"));
    }

    [Fact]
    public async Task CreateVersionAsync_UnchangedVersion_RepairsCorruptReferencedBlob()
    {
        var data = System.Text.Encoding.UTF8.GetBytes("referenced repair");
        var version = await _sut.CreateVersionAsync(
            ShareA, "same.txt", ToStream(data));
        var blobPath = Path.Combine(_versionRoot, version!.StoragePath);
        await File.WriteAllBytesAsync(blobPath, [1, 2, 3, 4]);

        var duplicate = await _sut.CreateVersionAsync(
            ShareA, "same.txt", ToStream(data));
        await using var restored = await _sut.ReadVersionAsync(
            ShareA, "same.txt", version.SnapshotTimestampUtc);
        using var copy = new MemoryStream();
        await restored.CopyToAsync(copy);

        Assert.Null(duplicate);
        Assert.Equal(data, copy.ToArray());
    }
}

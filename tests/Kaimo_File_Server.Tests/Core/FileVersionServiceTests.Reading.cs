using Kaimo_File_Server.Core.Domain;
using Kaimo_File_Server.Core.Repositories;
using Kaimo_File_Server.Core.Services.File;
using System.IO.Compression;
using System.Security.Cryptography;
using Moq;
using Xunit;

namespace Kaimo_File_Server.Tests;

public partial class FileVersionServiceTests
{
    // ═══════════════════════════════════════════
    //  Version Reading
    // ═══════════════════════════════════════════

    [Fact]
    public async Task ReadVersionAsync_ExistingVersion_ReturnsDecompressedContent()
    {
        var content = "Hello from version 1";
        var version = await _sut.CreateVersionAsync(ShareA, "doc.txt", ToStream(content));

        var stream = await _sut.ReadVersionAsync(ShareA, "doc.txt", version!.SnapshotTimestampUtc);

        using var reader = new StreamReader(stream);
        var result = await reader.ReadToEndAsync();

        Assert.Equal(content, result);
    }

    [Fact]
    public async Task ReadVersionAsync_BinaryContent_RoundTrips()
    {
        var data = new byte[4096];
        new Random(42).NextBytes(data);

        var version = await _sut.CreateVersionAsync(ShareA, "binary.bin", ToStream(data));

        var stream = await _sut.ReadVersionAsync(ShareA, "binary.bin", version!.SnapshotTimestampUtc);

        using var ms = new MemoryStream();
        await stream.CopyToAsync(ms);

        Assert.Equal(data, ms.ToArray());
    }

    [Fact]
    public async Task ReadVersionAsync_NonExistentVersion_ThrowsFileNotFound()
    {
        await Assert.ThrowsAsync<FileNotFoundException>(() =>
            _sut.ReadVersionAsync(ShareA, "nope.txt", DateTime.UtcNow));
    }

    [Fact]
    public async Task ReadVersionAsync_MultipleVersions_ReturnsCorrectOne()
    {
        var v1 = await _sut.CreateVersionAsync(ShareA, "doc.txt", ToStream("Version 1"));
        var v2 = await _sut.CreateVersionAsync(ShareA, "doc.txt", ToStream("Version 2"));

        // Read v1
        var stream1 = await _sut.ReadVersionAsync(ShareA, "doc.txt", v1!.SnapshotTimestampUtc);
        using var reader1 = new StreamReader(stream1);
        Assert.Equal("Version 1", await reader1.ReadToEndAsync());

        // Read v2
        var stream2 = await _sut.ReadVersionAsync(ShareA, "doc.txt", v2!.SnapshotTimestampUtc);
        using var reader2 = new StreamReader(stream2);
        Assert.Equal("Version 2", await reader2.ReadToEndAsync());
    }

    [Fact]
    public async Task ReadVersionAsync_ReturnedStreamIsSeekable()
    {
        var version = await _sut.CreateVersionAsync(ShareA, "doc.txt", ToStream("seekable"));

        var stream = await _sut.ReadVersionAsync(ShareA, "doc.txt", version!.SnapshotTimestampUtc);

        Assert.True(stream.CanSeek);
        Assert.True(stream.CanRead);
        Assert.Equal(0, stream.Position);
    }

    [Fact]
    public async Task ReadVersionAsync_LargeVersion_UsesDeleteOnCloseReadCache()
    {
        var data = new byte[9 * 1024 * 1024];
        new Random(42).NextBytes(data);
        var version = await _sut.CreateVersionAsync(ShareA, "large.bin", ToStream(data));

        var stream = await _sut.ReadVersionAsync(
            ShareA, "large.bin", version!.SnapshotTimestampUtc);

        Assert.True(stream.CanSeek);
        Assert.IsType<FileStream>(stream);
        var cacheDir = Path.Combine(_versionRoot, ".read-cache");
        Assert.Single(Directory.GetFiles(cacheDir, "*.tmp"));

        await stream.DisposeAsync();
        Assert.Empty(Directory.GetFiles(cacheDir, "*.tmp"));
    }

    [Fact]
    public async Task ReadVersionAsync_CorruptBlob_ThrowsAndReturnsNoPartialStream()
    {
        var version = await _sut.CreateVersionAsync(
            ShareA, "corrupt.txt", ToStream("valid content"));
        var blobPath = Path.Combine(_versionRoot, version!.StoragePath);
        await File.WriteAllBytesAsync(blobPath, [1, 2, 3, 4]);

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            _sut.ReadVersionAsync(
                ShareA, "corrupt.txt", version.SnapshotTimestampUtc));
    }

    // ═══════════════════════════════════════════
    //  Version Listing
    // ═══════════════════════════════════════════

    [Fact]
    public async Task GetVersionsAsync_ReturnsAllVersions()
    {
        await _sut.CreateVersionAsync(ShareA, "doc.txt", ToStream("V1"));
        await _sut.CreateVersionAsync(ShareA, "doc.txt", ToStream("V2"));
        await _sut.CreateVersionAsync(ShareA, "doc.txt", ToStream("V3"));

        var versions = await _sut.GetVersionsAsync(ShareA, "doc.txt");

        Assert.Equal(3, versions.Count);
    }

    [Fact]
    public async Task GetVersionsAsync_DifferentFile_ReturnsEmpty()
    {
        await _sut.CreateVersionAsync(ShareA, "a.txt", ToStream("content"));

        var versions = await _sut.GetVersionsAsync(ShareA, "b.txt");

        Assert.Empty(versions);
    }

    [Fact]
    public async Task GetSnapshotTimestampsAsync_ReturnsDistinctTimestamps()
    {
        await _sut.CreateVersionAsync(ShareA, "a.txt", ToStream("A1"));
        _time.Advance(TimeSpan.FromSeconds(1)); // distinct second across the two files
        await _sut.CreateVersionAsync(ShareA, "b.txt", ToStream("B1"));

        var timestamps = await _sut.GetSnapshotTimestampsAsync(ShareA);

        Assert.Equal(2, timestamps.Count);
    }

    [Fact]
    public async Task GetSnapshotTimestampsAsync_WithLimit_ReturnsOnlyTheNewest()
    {
        await _sut.CreateVersionAsync(ShareA, "folder/a.txt", ToStream("A1"));
        _time.Advance(TimeSpan.FromSeconds(1));
        await _sut.CreateVersionAsync(ShareA, "folder/b.txt", ToStream("B1"));
        var newest = _time.GetUtcNow().UtcDateTime;

        var timestamps = await _sut.GetSnapshotTimestampsAsync(ShareA, "folder", limit: 1);

        Assert.Equal(newest, Assert.Single(timestamps), TimeSpan.FromSeconds(1));
    }
}

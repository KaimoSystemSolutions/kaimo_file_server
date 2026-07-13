using Kaimo_File_Server.Core.Domain;
using Kaimo_File_Server.Core.Repositories;
using Kaimo_File_Server.Core.Services.File;
using System.IO.Compression;
using System.Security.Cryptography;
using Xunit;

namespace Kaimo_File_Server.Tests;

public class FileVersionServiceTests : IDisposable
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
            defaultMaxVersions: 5, defaultMaxAge: null, timeProvider: _time);
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

    // ═══════════════════════════════════════════
    //  Cross-Share Isolation (security regression)
    // ═══════════════════════════════════════════

    [Fact]
    public async Task GetVersionsAsync_SameRelativePathDifferentShares_AreIsolated()
    {
        // Both shares have a file at the same relative path.
        await _sut.CreateVersionAsync(ShareA, "report.docx", ToStream("Share A content"));
        await _sut.CreateVersionAsync(ShareB, "report.docx", ToStream("Share B content"));

        var aVersions = await _sut.GetVersionsAsync(ShareA, "report.docx");
        var bVersions = await _sut.GetVersionsAsync(ShareB, "report.docx");

        Assert.Single(aVersions);
        Assert.Single(bVersions);
        Assert.Equal(ShareA, aVersions[0].ShareId);
        Assert.Equal(ShareB, bVersions[0].ShareId);
    }

    [Fact]
    public async Task GetSnapshotTimestampsAsync_DoesNotLeakAcrossShares()
    {
        await _sut.CreateVersionAsync(ShareA, "report.docx", ToStream("A only"));

        // Share B never created a version for this path — it must see nothing.
        var bTimestamps = await _sut.GetSnapshotTimestampsAsync(ShareB);

        Assert.Empty(bTimestamps);
    }

    [Fact]
    public async Task ReadVersionAsync_CannotReadAnotherSharesVersion()
    {
        var aVersion = await _sut.CreateVersionAsync(ShareA, "report.docx", ToStream("Secret in share A"));

        // Share B asks for the exact timestamp of share A's version — must not resolve.
        await Assert.ThrowsAsync<FileNotFoundException>(() =>
            _sut.ReadVersionAsync(ShareB, "report.docx", aVersion!.SnapshotTimestampUtc));
    }

    [Fact]
    public async Task CreateVersionAsync_SameSecondDifferentShares_BothPersist()
    {
        // Two shares writing the same relative path at the same wall-clock second
        // must each get a version (previously blocked by a path-only unique key).
        var a = await _sut.CreateVersionAsync(ShareA, "report.docx", ToStream("A content"));
        var b = await _sut.CreateVersionAsync(ShareB, "report.docx", ToStream("B content"));

        Assert.NotNull(a);
        Assert.NotNull(b);
        Assert.Equal(a!.SnapshotTimestampUtc, b!.SnapshotTimestampUtc);
        Assert.Equal(1, a.VersionNumber);
        Assert.Equal(1, b.VersionNumber);
    }

    // ═══════════════════════════════════════════
    //  Retention Policy
    // ═══════════════════════════════════════════

    [Fact]
    public async Task Retention_TrimsToMaxVersions()
    {
        // Max is 5 (set in constructor). Advance the clock per write so timestamps
        // stay monotonic (mirrors real time; retention keeps the newest snapshots).
        for (int i = 0; i < 8; i++)
        {
            await _sut.CreateVersionAsync(ShareA, "doc.txt", ToStream($"Version {i}"));
            _time.Advance(TimeSpan.FromSeconds(1));
        }

        var versions = await _sut.GetVersionsAsync(ShareA, "doc.txt");

        Assert.True(versions.Count <= 5,
            $"Expected max 5 versions, got {versions.Count}");
    }

    [Fact]
    public async Task ApplyRetentionAsync_ManualCall_DeletesOldVersions()
    {
        // Create with a service that has high max so auto-trim doesn't fire
        var highMaxService = new FileVersionService(_repo,
            Path.Combine(_versionRoot, "ret"), defaultMaxVersions: 100, timeProvider: _time);

        for (int i = 0; i < 10; i++)
            await highMaxService.CreateVersionAsync(ShareA, "doc.txt", ToStream($"V{i}"));

        // Now manually trim to 3
        var deleted = await highMaxService.ApplyRetentionAsync(ShareA, "doc.txt", maxVersions: 3);

        Assert.True(deleted > 0);

        var remaining = await highMaxService.GetVersionsAsync(ShareA, "doc.txt");
        Assert.True(remaining.Count <= 3);
    }

    // ═══════════════════════════════════════════
    //  GetVersionAtAsync
    // ═══════════════════════════════════════════

    [Fact]
    public async Task GetVersionAtAsync_ExistingTimestamp_ReturnsVersion()
    {
        var version = await _sut.CreateVersionAsync(ShareA, "doc.txt", ToStream("content"));

        var found = await _sut.GetVersionAtAsync(ShareA, "doc.txt", version!.SnapshotTimestampUtc);

        Assert.NotNull(found);
        Assert.Equal(version.Id, found.Id);
    }

    [Fact]
    public async Task GetVersionAtAsync_WrongTimestamp_ReturnsNull()
    {
        await _sut.CreateVersionAsync(ShareA, "doc.txt", ToStream("content"));

        var found = await _sut.GetVersionAtAsync(ShareA, "doc.txt",
            new DateTime(2000, 1, 1, 0, 0, 0, DateTimeKind.Utc));

        Assert.Null(found);
    }

    [Fact]
    public async Task GetVersionAtAsync_WrongFile_ReturnsNull()
    {
        var version = await _sut.CreateVersionAsync(ShareA, "a.txt", ToStream("content"));

        var found = await _sut.GetVersionAtAsync(ShareA, "b.txt", version!.SnapshotTimestampUtc);

        Assert.Null(found);
    }
}

// ═══════════════════════════════════════════════════════════════
//  In-Memory Mock Repository (no EF Core / no DB required)
// ═══════════════════════════════════════════════════════════════

public class MockFileVersionRepository : IFileVersionRepository
{
    private readonly List<FileVersion> _versions = new();

    public Task<List<FileVersion>> GetVersionsAsync(Guid shareId, string filePath)
    {
        var result = _versions
            .Where(v => v.ShareId == shareId && v.FilePath == filePath)
            .OrderByDescending(v => v.SnapshotTimestampUtc)
            .ToList();
        return Task.FromResult(result);
    }

    public Task<FileVersion?> GetVersionAsync(Guid shareId, string filePath, DateTime snapshotTimestampUtc)
    {
        var result = _versions.FirstOrDefault(v =>
            v.ShareId == shareId &&
            v.FilePath == filePath &&
            v.SnapshotTimestampUtc == snapshotTimestampUtc);
        return Task.FromResult(result);
    }

    public Task<List<DateTime>> GetSnapshotTimestampsAsync(Guid shareId, string filePath)
    {
        var result = _versions
            .Where(v => v.ShareId == shareId && v.FilePath == filePath)
            .Select(v => v.SnapshotTimestampUtc)
            .Distinct()
            .OrderByDescending(t => t)
            .ToList();
        return Task.FromResult(result);
    }

    public Task<List<DateTime>> GetAllSnapshotTimestampsAsync(Guid shareId, string pathPrefix = "")
    {
        var query = _versions.Where(v => v.ShareId == shareId);
        if (!string.IsNullOrEmpty(pathPrefix))
            query = query.Where(v => v.FilePath.StartsWith(pathPrefix));

        var result = query
            .Select(v => v.SnapshotTimestampUtc)
            .Distinct()
            .OrderByDescending(t => t)
            .ToList();
        return Task.FromResult(result);
    }

    public Task<FileVersion> CreateAsync(FileVersion version)
    {
        _versions.Add(version);
        return Task.FromResult(version);
    }

    public Task<int> GetMaxVersionNumberAsync(Guid shareId, string filePath)
    {
        var max = _versions
            .Where(v => v.ShareId == shareId && v.FilePath == filePath)
            .Select(v => (int?)v.VersionNumber)
            .Max();
        return Task.FromResult(max ?? 0);
    }

    public Task<int> DeleteOlderThanAsync(Guid shareId, string filePath, DateTime cutoff)
    {
        var toRemove = _versions
            .Where(v => v.ShareId == shareId && v.FilePath == filePath && v.SnapshotTimestampUtc < cutoff)
            .ToList();
        foreach (var v in toRemove) _versions.Remove(v);
        return Task.FromResult(toRemove.Count);
    }

    public Task<int> TrimToMaxVersionsAsync(Guid shareId, string filePath, int maxCount)
    {
        var ordered = _versions
            .Where(v => v.ShareId == shareId && v.FilePath == filePath)
            .OrderByDescending(v => v.SnapshotTimestampUtc)
            .ToList();

        if (ordered.Count <= maxCount) return Task.FromResult(0);

        var toRemove = ordered.Skip(maxCount).ToList();
        foreach (var v in toRemove) _versions.Remove(v);
        return Task.FromResult(toRemove.Count);
    }

    public Task<bool> ExistsWithHashAsync(Guid shareId, string filePath, string contentHash)
    {
        var latestHash = _versions
            .Where(v => v.ShareId == shareId && v.FilePath == filePath)
            .OrderByDescending(v => v.SnapshotTimestampUtc)
            .Select(v => v.ContentHash)
            .FirstOrDefault();
        return Task.FromResult(latestHash == contentHash);
    }
}

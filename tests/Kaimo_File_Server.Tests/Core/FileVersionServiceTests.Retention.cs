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

        var blobs = Directory.GetFiles(
            Path.Combine(_versionRoot, "ret"), "*.bin.gz", SearchOption.AllDirectories);
        Assert.Equal(3, blobs.Length);
    }

    [Fact]
    public async Task DeletePathAsync_RemovesDirectoryHistoryAndUnreferencedBlobs()
    {
        await _sut.CreateVersionAsync(ShareA, "folder/a.txt", ToStream("A"));
        _time.Advance(TimeSpan.FromSeconds(1));
        await _sut.CreateVersionAsync(ShareA, "folder/nested/b.txt", ToStream("B"));
        _time.Advance(TimeSpan.FromSeconds(1));
        await _sut.CreateVersionAsync(ShareA, "keep.txt", ToStream("keep"));

        var deleted = await _sut.DeletePathAsync(ShareA, "folder");

        Assert.Equal(2, deleted);
        Assert.Empty(await _sut.GetFolderSnapshotAsync(ShareA, "folder", DateTime.MaxValue));
        Assert.Single(await _sut.GetVersionsAsync(ShareA, "keep.txt"));
        Assert.Single(Directory.GetFiles(_versionRoot, "*.bin.gz", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task DeletePathAsync_KeepsBlobWhileAnotherVersionReferencesIt()
    {
        var a = await _sut.CreateVersionAsync(ShareA, "a.txt", ToStream("shared"));
        _time.Advance(TimeSpan.FromSeconds(1));
        await _sut.CreateVersionAsync(ShareA, "b.txt", ToStream("shared"));
        var blob = Path.Combine(_versionRoot, a!.StoragePath);

        await _sut.DeletePathAsync(ShareA, "a.txt");
        Assert.True(File.Exists(blob));

        await _sut.DeletePathAsync(ShareA, "b.txt");
        Assert.False(File.Exists(blob));
    }

    [Fact]
    public async Task RenamePathAsync_MovesCompleteDirectoryHistory()
    {
        await _sut.CreateVersionAsync(ShareA, "old/a.txt", ToStream("A"));
        _time.Advance(TimeSpan.FromSeconds(1));
        await _sut.CreateVersionAsync(ShareA, "old/nested/b.txt", ToStream("B"));

        await _sut.RenamePathAsync(ShareA, "old", "new");

        Assert.Empty(await _sut.GetFolderSnapshotAsync(ShareA, "old", DateTime.MaxValue));
        Assert.Single(await _sut.GetVersionsAsync(ShareA, "new/a.txt"));
        Assert.Single(await _sut.GetVersionsAsync(ShareA, "new/nested/b.txt"));
    }

    [Fact]
    public async Task DeleteShareAsync_RemovesOnlyThatShareAndKeepsSharedBlobReferencedElsewhere()
    {
        var a = await _sut.CreateVersionAsync(ShareA, "doc.txt", ToStream("shared"));
        _time.Advance(TimeSpan.FromSeconds(1));
        await _sut.CreateVersionAsync(ShareB, "doc.txt", ToStream("shared"));
        var blob = Path.Combine(_versionRoot, a!.StoragePath);

        Assert.Equal(1, await _sut.DeleteShareAsync(ShareA));
        Assert.Empty(await _sut.GetVersionsAsync(ShareA, "doc.txt"));
        Assert.Single(await _sut.GetVersionsAsync(ShareB, "doc.txt"));
        Assert.True(File.Exists(blob));
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

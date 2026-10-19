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
    //  Folder Snapshot (point-in-time)
    // ═══════════════════════════════════════════

    [Fact]
    public async Task GetFolderSnapshotAsync_ReturnsLatestVersionPerFileAtOrBeforeCutoff()
    {
        // folder/a.txt: v1 @ t0, v2 @ t2 ; folder/b.txt: v1 @ t1
        await _sut.CreateVersionAsync(ShareA, "folder/a.txt", ToStream("A1"));

        _time.Advance(TimeSpan.FromSeconds(1));
        await _sut.CreateVersionAsync(ShareA, "folder/b.txt", ToStream("B1"));
        var cutoff = _time.GetUtcNow().UtcDateTime; // t1 — after a.v1 and b.v1, before a.v2

        _time.Advance(TimeSpan.FromSeconds(1));
        await _sut.CreateVersionAsync(ShareA, "folder/a.txt", ToStream("A2"));

        var snapshot = await _sut.GetFolderSnapshotAsync(ShareA, "folder", cutoff);

        Assert.Equal(2, snapshot.Count);
        var a = Assert.Single(snapshot, v => v.FilePath == "folder/a.txt");
        Assert.Equal(1, a.VersionNumber); // the older a.txt, not the post-cutoff v2
        Assert.Contains(snapshot, v => v.FilePath == "folder/b.txt");
    }

    [Fact]
    public async Task GetFolderSnapshotAsync_OmitsFilesCreatedAfterCutoff()
    {
        await _sut.CreateVersionAsync(ShareA, "folder/a.txt", ToStream("A1"));
        var cutoff = _time.GetUtcNow().UtcDateTime;

        _time.Advance(TimeSpan.FromSeconds(1));
        await _sut.CreateVersionAsync(ShareA, "folder/later.txt", ToStream("L1"));

        var snapshot = await _sut.GetFolderSnapshotAsync(ShareA, "folder", cutoff);

        Assert.Single(snapshot);
        Assert.Equal("folder/a.txt", snapshot[0].FilePath);
    }

    [Fact]
    public async Task GetFolderSnapshotAsync_PrefixDoesNotMatchSiblingFolder()
    {
        await _sut.CreateVersionAsync(ShareA, "folder/a.txt", ToStream("A1"));
        await _sut.CreateVersionAsync(ShareA, "folder2/b.txt", ToStream("B1"));
        var cutoff = _time.GetUtcNow().UtcDateTime.AddSeconds(1);

        var snapshot = await _sut.GetFolderSnapshotAsync(ShareA, "folder", cutoff);

        Assert.Single(snapshot);
        Assert.Equal("folder/a.txt", snapshot[0].FilePath);
    }

    [Fact]
    public async Task GetFolderSnapshotAsync_DoesNotLeakAcrossShares()
    {
        await _sut.CreateVersionAsync(ShareA, "folder/a.txt", ToStream("A only"));
        var cutoff = _time.GetUtcNow().UtcDateTime.AddSeconds(1);

        // Share B has nothing under this prefix — must not see share A's file.
        var snapshot = await _sut.GetFolderSnapshotAsync(ShareB, "folder", cutoff);

        Assert.Empty(snapshot);
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
}

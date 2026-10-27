using Kaimo_File_Server.Core.Services.File;
using Xunit;

namespace Kaimo_File_Server.Tests;

public partial class FileVersionServiceTests
{
    // ═══════════════════════════════════════════
    //  Blob garbage collection under failures
    // ═══════════════════════════════════════════

    /// <summary>The second reference check (after the blob was parked) hits a database outage.</summary>
    private sealed class FailingRecheckRepository : MockFileVersionRepository
    {
        public string? FailingPath { get; set; }
        private int _checks;

        public override async Task<bool> IsStoragePathReferencedAsync(string storagePath)
        {
            if (storagePath == FailingPath && _checks++ > 0)
                throw new TimeoutException("database unreachable");
            return await base.IsStoragePathReferencedAsync(storagePath);
        }
    }

    [Fact]
    public async Task DeletePathAsync_ReferenceRecheckFails_PutsParkedBlobBack()
    {
        var repo = new FailingRecheckRepository();
        var service = new FileVersionService(repo, _versionRoot, timeProvider: _time,
            diskSpaceProbe: _ => (1L << 50, 1L << 50));
        var version = await service.CreateVersionAsync(ShareA, "doc.txt", ToStream("payload"));
        repo.FailingPath = version!.StoragePath;

        await service.DeletePathAsync(ShareA, "doc.txt");

        // Unknown whether another process still needs it: keep it in place.
        Assert.True(File.Exists(Path.Combine(_versionRoot, version.StoragePath)));
        Assert.Empty(Directory.GetFiles(_versionRoot, "*.gc.tmp", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task ReconcileStorageAsync_ParkedBlobWithoutOriginal_IsMovedBack()
    {
        var poolRoot = Path.Combine(_testRoot, "pool", ".kaimo-versions");
        var service = CreatePoolService(new FakeLocator(new() { [ShareA] = poolRoot }));
        var version = await service.CreateVersionAsync(ShareA, "doc.txt", ToStream("payload"));
        var blob = Path.Combine(poolRoot, version!.StoragePath);
        // A process crashed between parking the blob and checking its references again;
        // the move kept the blob's old timestamp.
        var parked = $"{blob}.{Guid.NewGuid():N}.gc.tmp";
        File.Move(blob, parked);
        File.SetLastWriteTimeUtc(parked, DateTime.UtcNow.AddDays(-30));

        await service.ReconcileStorageAsync();

        Assert.True(File.Exists(blob));
        Assert.False(File.Exists(parked));
    }

    [Fact]
    public async Task ReconcileStorageAsync_StaleParkedBlobBesideOriginal_IsDeleted()
    {
        var poolRoot = Path.Combine(_testRoot, "pool", ".kaimo-versions");
        var service = CreatePoolService(new FakeLocator(new() { [ShareA] = poolRoot }));
        var version = await service.CreateVersionAsync(ShareA, "doc.txt", ToStream("payload"));
        var blob = Path.Combine(poolRoot, version!.StoragePath);
        var parked = $"{blob}.{Guid.NewGuid():N}.gc.tmp";
        File.Copy(blob, parked);
        File.SetLastWriteTimeUtc(parked, DateTime.UtcNow.AddDays(-30));

        await service.ReconcileStorageAsync();

        Assert.True(File.Exists(blob));
        Assert.False(File.Exists(parked));
    }

    // ═══════════════════════════════════════════
    //  Default policy and safe-save editors
    // ═══════════════════════════════════════════

    [Fact]
    public async Task DefaultPolicy_SafeSaveThroughTempFile_VersionsTheSavedDocument()
    {
        // Word: write ~WRD0000.tmp, close (snapshot), rename it over the document.
        var service = CreatePolicyService(VersioningPolicy.Default());

        await service.CaptureVersionAsync(ShareA, "~WRD0000.tmp", ToStream("new content"));
        await service.RenamePathAsync(ShareA, "~WRD0000.tmp", "letter.docx");

        Assert.Single(await _repo.GetVersionsAsync(ShareA, "letter.docx"));
    }
}

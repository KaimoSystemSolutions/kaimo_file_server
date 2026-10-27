using Kaimo_File_Server.Core.Domain;
using Kaimo_File_Server.Core.Services.File;
using Xunit;

namespace Kaimo_File_Server.Tests;

public partial class FileVersionServiceTests
{
    // ═══════════════════════════════════════════
    //  Global versioning policy
    // ═══════════════════════════════════════════

    private sealed class FakePolicyProvider(VersioningPolicy policy) : IVersioningPolicyProvider
    {
        public VersioningPolicy Policy { get; set; } = policy;
        public Task<VersioningPolicy> GetAsync() => Task.FromResult(Policy.Clone());
    }

    private FileVersionService CreatePolicyService(
        VersioningPolicy policy, long freeBytes = 1L << 50, IVersionStorageLocator? locator = null)
        => new(_repo, _versionRoot, timeProvider: _time, storageLocator: locator,
            diskSpaceProbe: _ => (freeBytes, 1L << 50),
            policyProvider: new FakePolicyProvider(policy));

    private int BlobCount() => Directory.Exists(_versionRoot)
        ? Directory.GetFiles(_versionRoot, "*.bin.gz", SearchOption.AllDirectories).Length
        : 0;

    [Fact]
    public async Task CaptureVersionAsync_NewContent_ReportsCreated()
    {
        var service = CreatePolicyService(VersioningPolicy.Default());

        var result = await service.CaptureVersionAsync(ShareA, "doc.txt", ToStream("v1"), "u");

        Assert.Equal(VersionCaptureOutcome.Created, result.Outcome);
        Assert.NotNull(result.Version);
        Assert.True(result.PreservesContent);
    }

    [Fact]
    public async Task CaptureVersionAsync_SameContent_ReportsUnchanged()
    {
        var service = CreatePolicyService(VersioningPolicy.Default());
        await service.CaptureVersionAsync(ShareA, "doc.txt", ToStream("v1"));

        var result = await service.CaptureVersionAsync(ShareA, "doc.txt", ToStream("v1"));

        Assert.Equal(VersionCaptureOutcome.Unchanged, result.Outcome);
        Assert.Null(result.Version);
        Assert.True(result.PreservesContent);
    }

    [Fact]
    public async Task CaptureVersionAsync_Disabled_StoresNothing()
    {
        var service = CreatePolicyService(new VersioningPolicy { Enabled = false });

        var result = await service.CaptureVersionAsync(ShareA, "doc.txt", ToStream("v1"));

        Assert.Equal(VersionCaptureOutcome.Disabled, result.Outcome);
        Assert.False(result.PreservesContent);
        Assert.Empty(await _repo.GetVersionsAsync(ShareA, "doc.txt"));
        Assert.Equal(0, BlobCount());
    }

    [Theory]
    [InlineData("~$report.docx")]
    [InlineData("folder/upload.tmp")]
    public async Task CaptureVersionAsync_ExcludedName_StoresNothing(string path)
    {
        var service = CreatePolicyService(VersioningPolicy.Default());

        var result = await service.CaptureVersionAsync(ShareA, path, ToStream("v1"));

        Assert.Equal(VersionCaptureOutcome.Excluded, result.Outcome);
        Assert.Empty(await _repo.GetVersionsAsync(ShareA, path));
        Assert.Equal(0, BlobCount());
    }

    [Fact]
    public async Task CaptureVersionAsync_LargerThanLimit_StoresNothing()
    {
        var service = CreatePolicyService(new VersioningPolicy { MaxFileSizeBytes = 10 });

        var result = await service.CaptureVersionAsync(ShareA, "big.bin", ToStream(new byte[11]));

        Assert.Equal(VersionCaptureOutcome.TooLarge, result.Outcome);
        Assert.Equal(0, BlobCount());
    }

    [Fact]
    public async Task CaptureVersionAsync_ExactlyAtLimit_IsVersioned()
    {
        var service = CreatePolicyService(new VersioningPolicy { MaxFileSizeBytes = 10 });

        var result = await service.CaptureVersionAsync(ShareA, "big.bin", ToStream(new byte[10]));

        Assert.Equal(VersionCaptureOutcome.Created, result.Outcome);
    }

    [Fact]
    public async Task CaptureVersionAsync_ZeroLimit_MeansUnlimited()
    {
        var service = CreatePolicyService(new VersioningPolicy { MaxFileSizeBytes = 0 });

        var result = await service.CaptureVersionAsync(ShareA, "big.bin", ToStream(new byte[4096]));

        Assert.Equal(VersionCaptureOutcome.Created, result.Outcome);
    }

    [Fact]
    public async Task CaptureVersionAsync_LowDiskSpace_ReportsLowSpace()
    {
        var service = CreatePolicyService(VersioningPolicy.Default(), freeBytes: 0);

        var result = await service.CaptureVersionAsync(ShareA, "doc.txt", ToStream("v1"));

        Assert.Equal(VersionCaptureOutcome.LowSpace, result.Outcome);
        Assert.False(result.PreservesContent);
    }

    [Fact]
    public async Task CaptureVersionAsync_ShareOutsidePool_ReportsNoPool()
    {
        var service = CreatePolicyService(VersioningPolicy.Default(), locator: new FakeLocator(new()));

        var result = await service.CaptureVersionAsync(ShareA, "doc.txt", ToStream("v1"));

        Assert.Equal(VersionCaptureOutcome.NoPool, result.Outcome);
    }

    [Fact]
    public async Task CreateVersionAsync_WithoutPolicyProvider_StillVersionsExcludedNames()
    {
        // Regression guard: without a provider the legacy behaviour applies unchanged.
        var version = await _sut.CreateVersionAsync(ShareA, "~$report.docx", ToStream("v1"));

        Assert.NotNull(version);
    }

    [Fact]
    public async Task CreateVersionAsync_ReadsThePolicyOnEveryCall()
    {
        var provider = new FakePolicyProvider(VersioningPolicy.Default());
        var service = new FileVersionService(_repo, _versionRoot, timeProvider: _time,
            diskSpaceProbe: _ => (1L << 50, 1L << 50), policyProvider: provider);

        Assert.NotNull(await service.CreateVersionAsync(ShareA, "doc.txt", ToStream("v1")));
        provider.Policy = new VersioningPolicy { Enabled = false };

        Assert.Null(await service.CreateVersionAsync(ShareA, "doc.txt", ToStream("v2")));
    }

    [Fact]
    public async Task WriteRetention_UsesThePolicyCap()
    {
        var service = CreatePolicyService(new VersioningPolicy
        {
            ThinningEnabled = false, MaxVersionsPerFile = 3, MaxAgeDays = 0,
        });

        for (var i = 0; i < 6; i++)
        {
            await service.CreateVersionAsync(ShareA, "doc.txt", ToStream($"v{i}"));
            _time.Advance(TimeSpan.FromSeconds(1));
        }

        Assert.Equal(3, (await service.GetVersionsAsync(ShareA, "doc.txt")).Count);
        Assert.Equal(3, BlobCount());
    }

    [Fact]
    public async Task ApplyRetentionAsync_WithoutArguments_AppliesPolicyAgeButKeepsNewest()
    {
        var service = CreatePolicyService(new VersioningPolicy { MaxAgeDays = 30 });
        await service.CreateVersionAsync(ShareA, "doc.txt", ToStream("old"));
        _time.Advance(TimeSpan.FromMinutes(1));
        var newest = await service.CreateVersionAsync(ShareA, "doc.txt", ToStream("newest"));
        _time.Advance(TimeSpan.FromDays(60));

        var removed = await service.ApplyRetentionAsync(ShareA, "doc.txt");

        Assert.Equal(1, removed);
        var remaining = Assert.Single(await service.GetVersionsAsync(ShareA, "doc.txt"));
        Assert.Equal(newest!.Id, remaining.Id);
        Assert.Equal(1, BlobCount());
    }

    [Fact]
    public async Task ApplyRetentionAsync_ExplicitArguments_KeepLegacySemantics()
    {
        var service = CreatePolicyService(VersioningPolicy.Default());
        for (var i = 0; i < 5; i++)
        {
            await service.CreateVersionAsync(ShareA, "doc.txt", ToStream($"v{i}"));
            _time.Advance(TimeSpan.FromSeconds(1));
        }

        var removed = await service.ApplyRetentionAsync(ShareA, "doc.txt", maxVersions: 2);

        Assert.Equal(3, removed);
        Assert.Equal(2, (await service.GetVersionsAsync(ShareA, "doc.txt")).Count);
    }

    // ═══════════════════════════════════════════
    //  Cross-process blob garbage collection
    // ═══════════════════════════════════════════

    /// <summary>
    /// Simulates another process that garbage-collects the blob after this process
    /// validated it but before its version row was inserted.
    /// </summary>
    private sealed class BlobStealingRepository(string blobRoot) : MockFileVersionRepository
    {
        public override Task<FileVersion> CreateAsync(FileVersion version)
        {
            var blob = Path.Combine(blobRoot, version.StoragePath);
            if (File.Exists(blob)) File.Delete(blob);
            return base.CreateAsync(version);
        }
    }

    [Fact]
    public async Task CreateVersionAsync_BlobCollectedConcurrently_RewritesBlobAfterInsert()
    {
        var repo = new BlobStealingRepository(_versionRoot);
        var service = new FileVersionService(repo, _versionRoot, timeProvider: _time,
            diskSpaceProbe: _ => (1L << 50, 1L << 50));

        var version = await service.CreateVersionAsync(ShareA, "doc.txt", ToStream("payload"));

        Assert.NotNull(version);
        await using var read = await service.ReadVersionAsync(ShareA, "doc.txt", version.SnapshotTimestampUtc);
        Assert.Equal("payload", await new StreamReader(read).ReadToEndAsync());
    }

    /// <summary>
    /// Simulates another process inserting a row for the blob between this process'
    /// reference check and its delete: the first check says "unreferenced", later ones see the row.
    /// </summary>
    private sealed class LateReferenceRepository : MockFileVersionRepository
    {
        public string? LateReferencedPath { get; set; }
        private int _checks;

        public override async Task<bool> IsStoragePathReferencedAsync(string storagePath)
        {
            if (storagePath == LateReferencedPath && _checks++ > 0) return true;
            return await base.IsStoragePathReferencedAsync(storagePath);
        }
    }

    [Fact]
    public async Task DeletePathAsync_BlobReferencedDuringDelete_KeepsBlob()
    {
        var repo = new LateReferenceRepository();
        var service = new FileVersionService(repo, _versionRoot, timeProvider: _time,
            diskSpaceProbe: _ => (1L << 50, 1L << 50));
        var version = await service.CreateVersionAsync(ShareA, "doc.txt", ToStream("payload"));
        repo.LateReferencedPath = version!.StoragePath;

        await service.DeletePathAsync(ShareA, "doc.txt");

        Assert.True(File.Exists(Path.Combine(_versionRoot, version.StoragePath)));
        Assert.Empty(Directory.GetFiles(_versionRoot, "*.tmp", SearchOption.AllDirectories));
    }
}

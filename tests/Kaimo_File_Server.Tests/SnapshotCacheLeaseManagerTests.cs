using Kaimo_File_Server.SmbBridge.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Kaimo_File_Server.Tests;

public sealed class SnapshotCacheLeaseManagerTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "kaimo-snapshot-leases-" + Guid.NewGuid().ToString("N"));
    private readonly Guid _shareId = Guid.NewGuid();
    private const string Token = "@GMT-2026.07.28-10.11.12";

    public SnapshotCacheLeaseManagerTests()
    {
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }

    [Fact]
    public async Task EvictionSkipsActiveMaterializationThenSucceeds()
    {
        var leases = new SnapshotCacheLeaseManager();
        using IDisposable materialization =
            await leases.AcquireMaterializationAsync(_root, _shareId, Token);

        Assert.Null(leases.TryAcquireEviction(
            _root, _shareId.ToString("N"), Token));

        materialization.Dispose();

        using IDisposable? eviction = leases.TryAcquireEviction(
            _root, _shareId.ToString("N"), Token);
        Assert.NotNull(eviction);
    }

    [Fact]
    public async Task SameTokenMaterializationsAreSerialized()
    {
        var leases = new SnapshotCacheLeaseManager();
        IDisposable first =
            await leases.AcquireMaterializationAsync(_root, _shareId, Token);

        Task<SnapshotCacheLeaseManager.MaterializationLease> secondTask = leases
            .AcquireMaterializationAsync(_root, _shareId, Token)
            .AsTask();
        await Task.Delay(50);
        Assert.False(secondTask.IsCompleted);

        first.Dispose();
        using IDisposable second = await secondTask.WaitAsync(
            TimeSpan.FromSeconds(2));
    }

    [Fact]
    public async Task DifferentTokensDoNotBlockEachOther()
    {
        var leases = new SnapshotCacheLeaseManager();
        using IDisposable first =
            await leases.AcquireMaterializationAsync(_root, _shareId, Token);

        using IDisposable second = await leases
            .AcquireMaterializationAsync(
                _root, _shareId, "@GMT-2026.07.28-10.12.12")
            .AsTask()
            .WaitAsync(TimeSpan.FromSeconds(2));
    }

    [Fact]
    public async Task CleanupLeavesActiveTokenAndDeletesItAfterRelease()
    {
        var leases = new SnapshotCacheLeaseManager();
        using IDisposable materialization =
            await leases.AcquireMaterializationAsync(_root, _shareId, Token);
        string tokenRoot = SnapshotCache.TokenRootFor(
            _root, _shareId.ToString("N"), Token);
        await File.WriteAllTextAsync(
            Path.Combine(tokenRoot, "projection.txt"), "data");

        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Snapshots:Cache:RootPath"] = _root
            })
            .Build();
        var cleanup = new SnapshotCacheCleanupService(
            Mock.Of<IServiceScopeFactory>(), configuration, leases,
            NullLogger<SnapshotCacheCleanupService>.Instance);

        Assert.False(cleanup.TryDeleteTokenDir(
            _shareId.ToString("N"), tokenRoot));
        Assert.True(Directory.Exists(tokenRoot));

        materialization.Dispose();

        Assert.True(cleanup.TryDeleteTokenDir(
            _shareId.ToString("N"), tokenRoot));
        Assert.False(Directory.Exists(tokenRoot));
    }

    [Fact]
    public void CleanupDirectoryEnumeration_ReturnsMaterializedSnapshot()
    {
        string first = Directory.CreateDirectory(
            Path.Combine(_root, "first")).FullName;
        string second = Directory.CreateDirectory(
            Path.Combine(_root, "second")).FullName;
        SnapshotCacheCleanupService cleanup = CreateCleanup(
            new SnapshotCacheLeaseManager());

        IReadOnlyList<string> snapshot =
            cleanup.SafeEnumerateDirectories(_root);
        Directory.CreateDirectory(Path.Combine(_root, "created-later"));

        Assert.Equal(
            [first, second],
            snapshot.Order(StringComparer.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task CleanupDirectoryEnumeration_ContainsDeferredIoFailure()
    {
        string fileInsteadOfDirectory = Path.Combine(_root, "projection.bin");
        await File.WriteAllTextAsync(fileInsteadOfDirectory, "data");
        SnapshotCacheCleanupService cleanup = CreateCleanup(
            new SnapshotCacheLeaseManager());

        IReadOnlyList<string> result =
            cleanup.SafeEnumerateDirectories(fileInsteadOfDirectory);

        Assert.Empty(result);
    }

    [Fact]
    public async Task HandoffBlocksEvictionUntilNativeAcknowledgement()
    {
        var leases = new SnapshotCacheLeaseManager();
        using SnapshotCacheLeaseManager.MaterializationLease materialization =
            await leases.AcquireMaterializationAsync(_root, _shareId, Token);
        string leaseId = materialization.PublishHandoff();

        Assert.Null(leases.TryAcquireEviction(
            _root, _shareId.ToString("N"), Token));
        Task<SnapshotCacheLeaseManager.MaterializationLease> nextTask = leases
            .AcquireMaterializationAsync(_root, _shareId, Token)
            .AsTask();
        await Task.Delay(50);
        Assert.False(nextTask.IsCompleted);

        Assert.True(leases.ReleaseHandoff(leaseId));
        Assert.False(leases.ReleaseHandoff(leaseId));
        using (SnapshotCacheLeaseManager.MaterializationLease next =
               await nextTask.WaitAsync(TimeSpan.FromSeconds(2)))
        {
        }

        using IDisposable? eviction = leases.TryAcquireEviction(
            _root, _shareId.ToString("N"), Token);
        Assert.NotNull(eviction);
    }

    [Fact]
    public async Task VerifiedExistingProjectionReusesSharedLeaseWithoutWaiting()
    {
        var leases = new SnapshotCacheLeaseManager();
        using SnapshotCacheLeaseManager.MaterializationLease materialization =
            await leases.AcquireMaterializationAsync(
                _root, _shareId, Token);
        string firstLeaseId = materialization.PublishHandoff();

        string? secondLeaseId = await leases
            .TryAcquireExistingHandoffAsync(
                _root, _shareId, Token,
                _ => ValueTask.FromResult(true))
            .AsTask()
            .WaitAsync(TimeSpan.FromSeconds(2));

        Assert.False(string.IsNullOrEmpty(secondLeaseId));
        Assert.NotEqual(firstLeaseId, secondLeaseId);
        Assert.True(leases.ReleaseHandoff(firstLeaseId));
        Assert.True(leases.ReleaseHandoff(secondLeaseId!));
    }

    [Fact]
    public async Task InvalidExistingProjectionFallsBackWithoutPublishingLease()
    {
        var leases = new SnapshotCacheLeaseManager();
        using SnapshotCacheLeaseManager.MaterializationLease materialization =
            await leases.AcquireMaterializationAsync(
                _root, _shareId, Token);
        string firstLeaseId = materialization.PublishHandoff();

        string? leaseId = await leases.TryAcquireExistingHandoffAsync(
            _root, _shareId, Token,
            _ => ValueTask.FromResult(false));

        Assert.Null(leaseId);
        Assert.True(leases.ReleaseHandoff(firstLeaseId));

        using SnapshotCacheLeaseManager.MaterializationLease next =
            await leases.AcquireMaterializationAsync(
                _root, _shareId, Token);
    }

    private SnapshotCacheCleanupService CreateCleanup(
        SnapshotCacheLeaseManager leases)
    {
        IConfiguration configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Snapshots:Cache:RootPath"] = _root
            })
            .Build();
        return new SnapshotCacheCleanupService(
            Mock.Of<IServiceScopeFactory>(), configuration, leases,
            NullLogger<SnapshotCacheCleanupService>.Instance);
    }
}

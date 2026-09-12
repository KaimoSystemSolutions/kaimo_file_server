using Kaimo_File_Server.Core.Domain;
using Kaimo_File_Server.Core.Domain.Identity;
using Kaimo_File_Server.Core.Repositories;
using Kaimo_File_Server.Core.Services;
using Kaimo_File_Server.Core.Services.DataServices;
using Kaimo_File_Server.Core.Services.File;
using Kaimo_File_Server.Core.Services.ExternalStorage;
using Kaimo_File_Server.Infrastructure.Clouds;
using Kaimo_File_Server.Web.Services;
using Microsoft.AspNetCore.DataProtection;
using Moq;
using Xunit;

namespace Kaimo_File_Server.Tests;

public sealed class CloudSyncExecutionServiceTests
{
    [Fact]
    public async Task RunAsync_ExecutesOptimizedRsyncTransportAndPersistsCompletion()
    {
        var share = new ShareDefinition("docs", "/data/docs");
        var definition = new SyncDefinition
        {
            ConnectionId = Guid.NewGuid(),
            LocalShareId = share.Id,
            LocalPath = "projects",
            RemotePath = "/nightly",
            Mode = SyncMode.Push,
            AdvancedSettings = new CloudSyncAdvancedSettings
            {
                MaxFileSizeBytes = 10_000,
                MaxUploadBytesPerSecond = 20_000,
                ExcludedExtensions = new HashSet<string> { ".tmp" }
            }
        };
        var storageConnection = new StorageConnection
        {
            Id = definition.ConnectionId,
            ProviderId = "rsync-ssh",
            Name = "Rsync test",
            State = StorageConnectionState.Ready
        };
        var shares = new Mock<IShareRepository>();
        shares.Setup(repository => repository.GetByIdAsync(share.Id)).ReturnsAsync(share);
        shares.Setup(repository => repository.UpdateCloudSyncRuntimeStateAsync(
                share.Id, "projects", It.IsAny<DateTime?>(),
                It.IsAny<IReadOnlyDictionary<string, string>>()))
            .ReturnsAsync(true);
        var definitions = new Mock<ISyncDefinitionRepository>();
        definitions.Setup(repository => repository.GetBySharePathAsync(
                share.Id, "projects", It.IsAny<CancellationToken>()))
            .ReturnsAsync(definition);
        var storageConnections = new Mock<IStorageConnectionRepository>();
        storageConnections.Setup(repository => repository.GetAsync(
                storageConnection.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(storageConnection);
        var optimized = new Mock<IOptimizedStorageSync>();
        var session = new Mock<IStorageSession>();
        session.SetupGet(candidate => candidate.OptimizedSync).Returns(optimized.Object);
        var provider = new Mock<IStorageConnectionProvider>();
        provider.SetupGet(candidate => candidate.Capabilities).Returns(
            StorageProviderCapabilities.Sync | StorageProviderCapabilities.OptimizedSync);
        provider.Setup(candidate => candidate.OpenSessionAsync(
                storageConnection, It.IsAny<CancellationToken>()))
            .ReturnsAsync(session.Object);
        var catalog = new Mock<IStorageConnectionProviderCatalog>();
        catalog.Setup(candidate => candidate.GetRequired("rsync-ssh")).Returns(provider.Object);
        var sut = new CloudSyncExecutionService(
            shares.Object,
            definitions.Object,
            storageConnections.Object,
            Mock.Of<ICredentialVault>(),
            Mock.Of<ILegacyCloudSyncMigrationService>(),
            Mock.Of<ICloudProviderFactory>(),
            Mock.Of<IFileServiceFactory>(),
            new InMemoryCloudSyncOperationCoordinator(TimeProvider.System),
            catalog.Object);
        var actor = new UserContext(
            new User(Guid.NewGuid(), "Admin", "admin", "hash", "nt"), [], [], []);

        var result = await sut.RunAsync(share.Id, "projects", actor);

        Assert.Equal(CloudSyncExecutionResult.Completed, result);
        optimized.Verify(candidate => candidate.SynchronizeAsync(
            It.Is<OptimizedSyncRequest>(request =>
                request.LocalRootPath == share.Path
                && request.LocalRelativePath == "projects"
                && request.RemotePath == "/nightly"
                && request.Direction == OptimizedSyncDirection.Push
                && request.MaximumFileSizeBytes == 10_000
                && request.MaximumTransferBytesPerSecond == 20_000
                && request.ExcludedExtensions!.Contains(".tmp")),
            It.IsAny<CancellationToken>()), Times.Once);
        definitions.Verify(repository => repository.MarkCompletedAsync(
            definition.Id,
            It.IsAny<DateTime>(),
            It.IsAny<IReadOnlyList<SyncFailure>?>(),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task RunAsync_HoldsSharedCoordinatorLeaseAndPersistsNarrowState()
    {
        var share = new ShareDefinition("docs", "/data/docs");
        var folder = new SyncedFolder(
            "google", new Dictionary<string, string>(), "/remote")
        {
            Mode = SyncMode.TwoWay
        };
        share.CloudSettings.Folders["projects"] = folder;
        var definition = new SyncDefinition
        {
            ConnectionId = Guid.NewGuid(),
            LocalShareId = share.Id,
            LocalPath = "projects",
            RemotePath = "/remote",
            Mode = SyncMode.TwoWay
        };
        var storageConnection = new StorageConnection
        {
            Id = definition.ConnectionId,
            ProviderId = "google",
            Name = "Google test",
            State = StorageConnectionState.Ready
        };
        var vault = new DataProtectionCredentialVault(new EphemeralDataProtectionProvider());
        storageConnection.EncryptedCredentialPayload = vault.ProtectConnectionCredentials(
            storageConnection, new Dictionary<string, string>());
        var shares = new Mock<IShareRepository>();
        shares.Setup(repository => repository.GetByIdAsync(share.Id))
            .ReturnsAsync(share);
        shares.Setup(repository => repository.UpdateCloudSyncRuntimeStateAsync(
                share.Id,
                "projects",
                It.IsAny<DateTime?>(),
                It.IsAny<IReadOnlyDictionary<string, string>>()))
            .ReturnsAsync(true);

        var started = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var connection = new Mock<ICloudConnection>();
        connection.Setup(cloud => cloud.SyncAsync(
                It.IsAny<IFileService>(),
                It.IsAny<UserContext>(),
                "/remote",
                "projects",
                SyncMode.TwoWay,
                It.IsAny<CloudSyncTransferOptions?>(),
                It.IsAny<SyncManifest?>(),
                It.IsAny<Action<string?, int>?>(),
                It.IsAny<ICollection<SyncFailure>?>(),
                It.IsAny<CancellationToken>()))
            .Returns(async () =>
            {
                started.SetResult();
                await release.Task;
                return (SyncManifest?)null;
            });
        var providers = new Mock<ICloudProviderFactory>();
        providers.Setup(factory => factory.CreateOrLoad(
                share.Id,
                It.Is<SyncedFolder>(candidate =>
                    candidate.Provider == "google" &&
                    candidate.RemotePath == "/remote")))
            .Returns(connection.Object);
        var files = new Mock<IFileServiceFactory>();
        files.Setup(factory => factory.CreateForShare(share.Id, share.Path))
            .Returns(Mock.Of<IFileService>());
        var operations = new InMemoryCloudSyncOperationCoordinator(TimeProvider.System);
        var definitions = new Mock<ISyncDefinitionRepository>();
        definitions.Setup(repository => repository.GetBySharePathAsync(
                share.Id, "projects", It.IsAny<CancellationToken>()))
            .ReturnsAsync(definition);
        var storageConnections = new Mock<IStorageConnectionRepository>();
        storageConnections.Setup(repository => repository.GetAsync(
                storageConnection.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(storageConnection);
        var migration = new Mock<ILegacyCloudSyncMigrationService>();
        var sut = new CloudSyncExecutionService(
            shares.Object,
            definitions.Object,
            storageConnections.Object,
            vault,
            migration.Object,
            providers.Object,
            files.Object,
            operations);
        var user = new User(
            Guid.NewGuid(), "Scheduler", "scheduler", "hash", "nt");
        var actor = new UserContext(user, [], [], []);

        Task<CloudSyncExecutionResult> run = sut.RunAsync(
            share.Id, "projects", actor);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Null(await operations.TryBeginPathMutationAsync(
            share.Id, "projects/sub", "archive/sub"));

        release.SetResult();
        Assert.Equal(CloudSyncExecutionResult.Completed, await run);
        var after = await operations.TryBeginPathMutationAsync(
            share.Id, "projects/sub", "archive/sub");
        Assert.NotNull(after);
        await after.DisposeAsync();
        shares.Verify(repository => repository.UpdateCloudSyncRuntimeStateAsync(
            share.Id,
            "projects",
            It.IsAny<DateTime?>(),
            It.IsAny<IReadOnlyDictionary<string, string>>()), Times.Once);
        shares.Verify(repository => repository.UpdateAsync(
            It.IsAny<ShareDefinition>()), Times.Never);
        definitions.Verify(repository => repository.MarkCompletedAsync(
            definition.Id,
            It.IsAny<DateTime>(),
            It.IsAny<IReadOnlyList<SyncFailure>?>(),
            It.IsAny<CancellationToken>()), Times.Once);
    }
}

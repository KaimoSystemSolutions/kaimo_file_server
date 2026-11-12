using Kaimo_File_Server.Core.Domain;
using Kaimo_File_Server.Core.Domain.Identity;
using Kaimo_File_Server.Core.Repositories.ExternalStorage;
using Kaimo_File_Server.Core.Repositories.Shares;
using Kaimo_File_Server.Core.Security.Credentials;
using Kaimo_File_Server.Core.Services.CloudSync;
using Kaimo_File_Server.Core.Services.ExternalStorage;
using Kaimo_File_Server.Core.Services.File;
using Kaimo_File_Server.Web.Services;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;
using Kaimo_File_Server.Infrastructure.ExternalStorage.Cloud;
using Kaimo_File_Server.Infrastructure.ExternalStorage.CloudSync;

namespace Kaimo_File_Server.Infrastructure.Tests.ExternalStorage.CloudSync;

/// <summary>
/// A run that completes with failed items reports them in the log as well: the status page only
/// keeps the first few, and a file that could not be copied must be traceable afterwards.
/// </summary>
public sealed class CloudSyncExecutionServiceFailureLoggingTests
{
    private readonly CapturingLogger _logger = new();

    private async Task<CloudSyncExecutionResult> RunWithFailuresAsync(params SyncFailure[] itemFailures)
    {
        var share = new ShareDefinition("docs", "/data/docs");
        share.CloudSettings.Folders["projects"] = new SyncedFolder(
            "google", new Dictionary<string, string>(), "/remote") { Mode = SyncMode.Pull };
        var definition = new SyncDefinition
        {
            ConnectionId = Guid.NewGuid(),
            LocalShareId = share.Id,
            LocalPath = "projects",
            RemotePath = "/remote",
            Mode = SyncMode.Pull
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
        shares.Setup(r => r.GetByIdAsync(share.Id)).ReturnsAsync(share);
        shares.Setup(r => r.UpdateCloudSyncRuntimeStateAsync(
                share.Id, "projects", It.IsAny<DateTime?>(), It.IsAny<IReadOnlyDictionary<string, string>>()))
            .ReturnsAsync(true);
        var connection = new Mock<ICloudConnection>();
        connection.Setup(c => c.SyncAsync(
                It.IsAny<IFileService>(), It.IsAny<UserContext>(), "/remote", "projects", SyncMode.Pull,
                It.IsAny<CloudSyncTransferOptions?>(), It.IsAny<SyncManifest?>(), It.IsAny<Action<string?, int>?>(),
                It.IsAny<ICollection<SyncFailure>?>(), It.IsAny<CancellationToken>()))
            .Callback((IFileService _, UserContext _, string _, string _, SyncMode _, CloudSyncTransferOptions? _,
                SyncManifest? _, Action<string?, int>? _, ICollection<SyncFailure>? failures, CancellationToken _) =>
            {
                foreach (SyncFailure failure in itemFailures)
                    failures!.Add(failure);
            })
            .ReturnsAsync((SyncManifest?)null);
        var providers = new Mock<ICloudProviderFactory>();
        providers.Setup(f => f.CreateOrLoad(share.Id, It.IsAny<SyncedFolder>())).Returns(connection.Object);
        var files = new Mock<IFileServiceFactory>();
        files.Setup(f => f.CreateForShare(It.IsAny<ShareDefinition>())).Returns(Mock.Of<IFileService>());
        var definitions = new Mock<ISyncDefinitionRepository>();
        definitions.Setup(r => r.GetBySharePathAsync(share.Id, "projects", It.IsAny<CancellationToken>()))
            .ReturnsAsync(definition);
        var connections = new Mock<IStorageConnectionRepository>();
        connections.Setup(r => r.GetAsync(storageConnection.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(storageConnection);

        var sut = new CloudSyncExecutionService(
            shares.Object, definitions.Object, connections.Object, vault,
            Mock.Of<ILegacyCloudSyncMigrationService>(), providers.Object, files.Object,
            new InMemoryCloudSyncOperationCoordinator(TimeProvider.System),
            logger: _logger);
        var actor = new UserContext(new User(Guid.NewGuid(), "Admin", "admin", "hash", "nt"), [], [], []);

        return await sut.RunAsync(share.Id, "projects", actor);
    }

    private IReadOnlyList<string> Warnings
        => _logger.Entries.Where(e => e.Level == LogLevel.Warning).Select(e => e.Message).ToArray();

    [Fact]
    public async Task RunAsync_FailedItem_IsLoggedWithPathOperationAndReason()
    {
        var result = await RunWithFailuresAsync(new SyncFailure(
            "users/Remuxen.mp4", SyncFailureOperation.Download,
            "The protocol helper failed with exit code 1: NT_STATUS_DISK_FULL"));

        Assert.Equal(CloudSyncExecutionResult.CompletedWithErrors, result);
        string warning = Assert.Single(Warnings);
        Assert.Contains("users/Remuxen.mp4", warning);
        Assert.Contains("Download", warning);
        Assert.Contains("NT_STATUS_DISK_FULL", warning);
    }

    [Fact]
    public async Task RunAsync_WithoutFailures_LogsNoWarning()
    {
        var result = await RunWithFailuresAsync();

        Assert.Equal(CloudSyncExecutionResult.Completed, result);
        Assert.Empty(Warnings);
    }

    [Fact]
    public async Task RunAsync_ManyFailures_CapsTheLogAndReportsTheRest()
    {
        // A dead target can fail every file of a huge tree; the log must not get one line per file.
        SyncFailure[] failures = Enumerable.Range(0, 105)
            .Select(i => new SyncFailure($"f{i}", SyncFailureOperation.Download, "failed"))
            .ToArray();

        await RunWithFailuresAsync(failures);

        Assert.Equal(101, Warnings.Count);
        Assert.Contains(" 5 more", Warnings[^1]);
    }

    private sealed class CapturingLogger : ILogger<CloudSyncExecutionService>
    {
        public List<(LogLevel Level, string Message)> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
            => Entries.Add((logLevel, formatter(state, exception)));
    }
}

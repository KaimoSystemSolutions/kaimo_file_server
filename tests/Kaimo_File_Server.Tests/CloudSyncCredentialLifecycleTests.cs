using System.Net;
using Kaimo_File_Server.Core.Domain;
using Kaimo_File_Server.Core.Domain.Identity;
using Kaimo_File_Server.Core.Repositories;
using Kaimo_File_Server.Core.Services;
using Kaimo_File_Server.Core.Services.DataServices;
using Kaimo_File_Server.Core.Services.File;
using Kaimo_File_Server.Infrastructure.Clouds;
using Kaimo_File_Server.Web.Services;
using Microsoft.AspNetCore.DataProtection;
using Moq;
using Xunit;

namespace Kaimo_File_Server.Tests;

/// <summary>
/// Token lifecycle during cloud syncs: a rotated token is written back only onto
/// the connection version the run started from, a re-authorized grant replaces
/// the cached provider connection, and a revoked grant parks the connection
/// instead of being retried forever.
/// </summary>
public sealed class CloudSyncCredentialLifecycleTests
{
    private readonly ShareDefinition _share = new("docs", "/data/docs");
    private readonly SyncDefinition _definition;
    private readonly StorageConnection _storageConnection;
    private readonly DataProtectionCredentialVault _vault = new(new EphemeralDataProtectionProvider());
    private readonly Mock<IStorageConnectionRepository> _connections = new();
    private readonly Mock<ICloudConnection> _cloud = new();

    public CloudSyncCredentialLifecycleTests()
    {
        _definition = new SyncDefinition
        {
            ConnectionId = Guid.NewGuid(), LocalShareId = _share.Id, LocalPath = "projects",
            RemotePath = "/remote", Mode = SyncMode.Pull
        };
        _storageConnection = new StorageConnection
        {
            Id = _definition.ConnectionId, ProviderId = "onedrive", Name = "OneDrive",
            State = StorageConnectionState.Ready, ConcurrencyVersion = 7
        };
        _storageConnection.EncryptedCredentialPayload = _vault.ProtectConnectionCredentials(
            _storageConnection, new Dictionary<string, string> { ["refreshToken"] = "t1" });
        _connections.Setup(r => r.GetAsync(_storageConnection.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => Clone(_storageConnection));
    }

    // ── R3: conditional write-back ──

    [Fact]
    public async Task RotatedToken_IsWrittenBackOnTheVersionTheRunStartedFrom()
    {
        ArrangeRotation("t2");
        _connections.Setup(r => r.TryUpdateCredentialAsync(
                _storageConnection.Id, 7, It.IsAny<string>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        await Sut().RunAsync(_share.Id, "projects", Actor());

        _cloud.Verify(c => c.AcknowledgeCredentialChanges(), Times.Once);
        // The state (e.g. Disabled) is never rewritten by a sync run.
        _connections.Verify(r => r.UpdateRuntimeAsync(
            It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<string?>(),
            It.IsAny<StorageConnectionState>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task RotatedToken_IsNotForcedOntoAConcurrentlyChangedConnection()
    {
        ArrangeRotation("t2");
        _connections.Setup(r => r.TryUpdateCredentialAsync(
                It.IsAny<Guid>(), It.IsAny<long>(), It.IsAny<string>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);

        await Sut().RunAsync(_share.Id, "projects", Actor());

        // Still pending, so it is retried later instead of silently dropped.
        _cloud.Verify(c => c.AcknowledgeCredentialChanges(), Times.Never);
        _connections.Verify(r => r.SaveAsync(It.IsAny<StorageConnection>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    // ── R5: revoked grant ──

    [Fact]
    public async Task AuthenticationFailure_ParksConnectionForReauthorization()
    {
        ArrangeSyncThrows(new ProviderRequestException("onedrive", new SanitizedProviderError(
            "invalid_grant", ProviderErrorCategory.Authentication, HttpStatusCode.BadRequest)));
        StorageConnection? saved = null;
        _connections.Setup(r => r.SaveAsync(It.IsAny<StorageConnection>(), It.IsAny<CancellationToken>()))
            .Callback<StorageConnection, CancellationToken>((c, _) => saved = c)
            .Returns(Task.CompletedTask);

        await Assert.ThrowsAsync<ProviderRequestException>(() => Sut().RunAsync(_share.Id, "projects", Actor()));

        Assert.NotNull(saved);
        Assert.Equal(StorageConnectionState.NeedsReauthorization, saved.State);
        Assert.Equal("invalid_grant", saved.LastErrorCode);
    }

    [Fact]
    public async Task ParkedConnection_IsNotRetriedByTheNextRun()
    {
        _storageConnection.State = StorageConnectionState.NeedsReauthorization;

        var result = await Sut().RunAsync(_share.Id, "projects", Actor());

        Assert.Equal(CloudSyncExecutionResult.Missing, result);
        _cloud.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task TransientFailure_DoesNotParkConnection()
    {
        ArrangeSyncThrows(new ProviderRequestException("onedrive", new SanitizedProviderError(
            "service_unavailable", ProviderErrorCategory.Transient, HttpStatusCode.ServiceUnavailable)));

        await Assert.ThrowsAsync<ProviderRequestException>(() => Sut().RunAsync(_share.Id, "projects", Actor()));

        _connections.Verify(r => r.SaveAsync(It.IsAny<StorageConnection>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    // ── R4: provider connection cache ──

    [Fact]
    public void Cache_ReplacesConnectionAfterReauthorization()
    {
        var provider = new CountingProvider();
        var factory = new CloudProviderFactory([provider]);
        string connectionId = Guid.NewGuid().ToString("D");
        var first = factory.CreateOrLoad(_share.Id, Folder(connectionId, "old-grant"));

        // Next run reads a different grant from the vault (the admin re-authorized).
        var second = factory.CreateOrLoad(_share.Id, Folder(connectionId, "new-grant"));

        Assert.NotSame(first, second);
        Assert.Equal(2, provider.Created);
    }

    [Fact]
    public void Cache_KeepsConnectionAfterOwnRotationWasPersisted()
    {
        var provider = new CountingProvider();
        var factory = new CloudProviderFactory([provider]);
        string connectionId = Guid.NewGuid().ToString("D");
        var runOne = Folder(connectionId, "t1");
        var first = factory.CreateOrLoad(_share.Id, runOne);
        runOne.Data["refreshToken"] = "t2";   // the provider rotates in place …
        runOne.Data["scope"] = "default";     // … and may add unpersisted defaults

        // … the rotated token was written back, so the next run reads t2.
        var second = factory.CreateOrLoad(_share.Id, Folder(connectionId, "t2"));

        Assert.Same(first, second);
        Assert.Equal(1, provider.Created);
    }

    private static SyncedFolder Folder(string connectionId, string refreshToken)
        => new("fake", new Dictionary<string, string>
        {
            ["connectionId"] = connectionId,
            ["refreshToken"] = refreshToken
        });

    private void ArrangeRotation(string rotated)
    {
        ArrangeSyncReturns();
        _cloud.SetupGet(c => c.HasPendingCredentialChanges).Returns(true);
        _cloud.Setup(c => c.GetPendingCredentialChanges())
            .Returns(new Dictionary<string, string> { ["refreshToken"] = rotated });
    }

    private void ArrangeSyncReturns()
        => SetupSync().ReturnsAsync((SyncManifest?)null);

    private void ArrangeSyncThrows(Exception exception)
        => SetupSync().ThrowsAsync(exception);

    private Moq.Language.Flow.ISetup<ICloudConnection, Task<SyncManifest?>> SetupSync()
        => _cloud.Setup(c => c.SyncAsync(
            It.IsAny<IFileService>(), It.IsAny<UserContext>(), It.IsAny<string>(), It.IsAny<string>(),
            It.IsAny<SyncMode>(), It.IsAny<CloudSyncTransferOptions?>(), It.IsAny<SyncManifest?>(),
            It.IsAny<Action<string?, int>?>(), It.IsAny<ICollection<SyncFailure>?>(), It.IsAny<CancellationToken>()));

    private CloudSyncExecutionService Sut()
    {
        var shares = new Mock<IShareRepository>();
        shares.Setup(r => r.GetByIdAsync(_share.Id)).ReturnsAsync(_share);
        var definitions = new Mock<ISyncDefinitionRepository>();
        definitions.Setup(r => r.GetBySharePathAsync(_share.Id, "projects", It.IsAny<CancellationToken>()))
            .ReturnsAsync(_definition);
        var providers = new Mock<ICloudProviderFactory>();
        providers.Setup(f => f.CreateOrLoad(_share.Id, It.IsAny<SyncedFolder>())).Returns(_cloud.Object);
        var files = new Mock<IFileServiceFactory>();
        files.Setup(f => f.CreateForShare(_share.Id, _share.Path)).Returns(Mock.Of<IFileService>());
        return new CloudSyncExecutionService(
            shares.Object, definitions.Object, _connections.Object, _vault,
            Mock.Of<ILegacyCloudSyncMigrationService>(), providers.Object, files.Object,
            new InMemoryCloudSyncOperationCoordinator(TimeProvider.System));
    }

    private static UserContext Actor()
        => new(new User(Guid.NewGuid(), "Scheduler", "scheduler", "hash", "nt"), [], [], []);

    private static StorageConnection Clone(StorageConnection source) => new()
    {
        Id = source.Id, ProviderId = source.ProviderId, Name = source.Name, State = source.State,
        ConcurrencyVersion = source.ConcurrencyVersion,
        EncryptedCredentialPayload = source.EncryptedCredentialPayload
    };

    private sealed class CountingProvider : ICloudProvider
    {
        public int Created { get; private set; }
        public string Id => "fake";
        public string DisplayName => "Fake";
        public string AuthorizationEndpoint => "/api/fake/connect";

        public ICloudConnection CreateConnection(Guid shareId, SyncedFolder folder)
        {
            Created++;
            return Mock.Of<ICloudConnection>();
        }
    }
}

using Grpc.Core;
using Kaimo_File_Server.Core.Repositories;
using Kaimo_File_Server.Core.Logging;
using Kaimo_File_Server.Core.Security;
using Kaimo_File_Server.Core.Services.DataServices;
using Kaimo_File_Server.SmbBridge.Grpc;
using Kaimo_File_Server.SmbBridge.Security;
using Kaimo_File_Server.SmbBridge.Services;
using Kaimo_File_Server.Infrastructure.Logging;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Xunit;

namespace Kaimo_File_Server.Tests;

public sealed class BridgeRpcCancellationTests
{
    [Fact]
    public async Task GetNtHash_CancellationInterruptsAuthenticationLookup()
    {
        var auth = new Mock<IAuthenticationLookup>();
        auth.Setup(x => x.GetNtHashAsync("alice"))
            .Returns(new TaskCompletionSource<byte[]?>(
                TaskCreationOptions.RunContinuationsAsynchronously).Task);
        var sut = new AuthGrpcService(
            auth.Object, Mock.Of<IUserRepository>(),
            new HashExportRateLimiter(
                Options.Create(new HashExportRateLimitOptions())),
            NullLogger<AuthGrpcService>.Instance);

        await AssertRpcCancellationAsync(context => sut.GetNtHash(
            new GetNtHashRequest { Username = "alice" }, context));
    }

    [Fact]
    public async Task ListShares_CancellationInterruptsRepositoryLookup()
    {
        var shares = new Mock<IShareRepository>();
        shares.Setup(x => x.GetAllEnabledAsync())
            .Returns(new TaskCompletionSource<List<Core.Domain.ShareDefinition>>(
                TaskCreationOptions.RunContinuationsAsynchronously).Task);
        var sut = new ShareGrpcService(
            shares.Object, NullLogger<ShareGrpcService>.Instance);

        await AssertRpcCancellationAsync(context => sut.ListShares(
            new ListSharesRequest(), context));
    }

    [Fact]
    public async Task GetProtocolSettings_CancellationInterruptsConfigLookup()
    {
        var config = new Mock<ISmbConfigStore>();
        config.Setup(x => x.GetProtocolSettingsAsync())
            .Returns(new TaskCompletionSource<SmbProtocolSettings>(
                TaskCreationOptions.RunContinuationsAsynchronously).Task);
        var sut = new ConfigGrpcService(
            config.Object, Mock.Of<ILoggingConfigStore>(),
            new LoggingLevelConfigurationSource(),
            NullLogger<ConfigGrpcService>.Instance);

        await AssertRpcCancellationAsync(context => sut.GetProtocolSettings(
            new GetProtocolSettingsRequest(), context));
        config.Verify(x => x.IsSmbEnabledAsync(), Times.Never);
    }

    [Fact]
    public async Task AuthorizeOpen_CancellationInterruptsUserResolution()
    {
        var auth = new Mock<IAuthenticationLookup>();
        auth.Setup(x => x.ResolveUserContextAsync("alice"))
            .Returns(new TaskCompletionSource<Core.Domain.Identity.UserContext?>(
                TaskCreationOptions.RunContinuationsAsynchronously).Task);
        var sut = new AuthzGrpcService(
            Mock.Of<IUserRepository>(), Mock.Of<IShareRepository>(),
            auth.Object, Mock.Of<IAclService>(), Mock.Of<ISmbConfigStore>(),
            NullLogger<AuthzGrpcService>.Instance);

        await AssertRpcCancellationAsync(context => sut.AuthorizeOpen(
            new AuthorizeOpenRequest
            {
                Username = "alice",
                Share = "share",
                Path = "file.txt",
                AccessMask = 1
            },
            context));
    }

    [Fact]
    public async Task EnumerateSnapshots_CancellationInterruptsUserResolution()
    {
        var auth = new Mock<IAuthenticationLookup>();
        auth.Setup(x => x.ResolveUserContextAsync("alice"))
            .Returns(new TaskCompletionSource<Core.Domain.Identity.UserContext?>(
                TaskCreationOptions.RunContinuationsAsynchronously).Task);
        IConfiguration configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Snapshots:Cache:RootPath"] = Path.Combine(
                    Path.GetTempPath(), "kaimo-cancellation-cache")
            })
            .Build();
        var sut = new SnapshotGrpcService(
            Mock.Of<IShareRepository>(), auth.Object, Mock.Of<IAclService>(),
            Mock.Of<Core.Services.File.IFileVersionService>(),
            Mock.Of<Core.Services.File.IFileServiceFactory>(),
            new SnapshotCacheLeaseManager(),
            new SnapshotMaterializationLimiter(configuration), configuration,
            NullLogger<SnapshotGrpcService>.Instance);

        await AssertRpcCancellationAsync(context => sut.EnumerateSnapshots(
            new EnumerateSnapshotsRequest
            {
                Username = "alice",
                Share = "share",
                Path = ""
            },
            context));
    }

    [Fact]
    public async Task LifecycleCancellationAfterMutation_CompletesReceipt()
    {
        using var cancellation = new CancellationTokenSource();
        var context = new CancellationServerCallContext(cancellation.Token);
        var user = new Core.Domain.Identity.User(
            Guid.NewGuid(), "Alice", "alice", "hash", "nt");
        var userContext = new Core.Domain.Identity.UserContext(
            user, [], [], []);
        var share = new Core.Domain.ShareDefinition(
            "share", Path.GetTempPath(), isEnabled: true);
        var auth = new Mock<IAuthenticationLookup>();
        auth.Setup(x => x.ResolveUserContextAsync("alice"))
            .ReturnsAsync(userContext);
        var shares = new Mock<IShareRepository>();
        shares.Setup(x => x.GetByNameAsync("share")).ReturnsAsync(share);
        var files = new Mock<Core.Services.File.IFileService>();
        files.Setup(x => x.NotifyExternalMkdirAsync("folder", userContext))
            .Returns(() =>
            {
                cancellation.Cancel();
                return Task.CompletedTask;
            });
        var factory = new Mock<Core.Services.File.IFileServiceFactory>();
        factory.Setup(x => x.CreateForShare(share.Id, share.Path))
            .Returns(files.Object);
        Guid eventId = Guid.NewGuid();
        var receipts = new Mock<ISambaLifecycleEventRepository>();
        receipts.Setup(x => x.TryClaimAsync(
                eventId, "mkdir", It.IsAny<TimeSpan>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(SambaEventClaimResult.Acquired);
        receipts.Setup(x => x.CompleteAsync(
                eventId, CancellationToken.None))
            .Returns(Task.CompletedTask);
        var sut = new FileEventGrpcService(
            factory.Object, shares.Object, auth.Object, receipts.Object,
            NullLogger<FileEventGrpcService>.Instance);

        NotifyReply reply = await sut.NotifyMkdir(
            new NotifyPathRequest
            {
                Username = "alice",
                Share = "share",
                Path = "folder",
                IsDirectory = true,
                EventId = eventId.ToString("N")
            },
            context);

        Assert.True(reply.Ok);
        receipts.Verify(x => x.CompleteAsync(
            eventId, CancellationToken.None), Times.Once);
        receipts.Verify(x => x.ReleaseAsync(
            It.IsAny<Guid>(), It.IsAny<string>(),
            It.IsAny<CancellationToken>()), Times.Never);
    }

    private static async Task AssertRpcCancellationAsync(
        Func<ServerCallContext, Task> invoke)
    {
        using var cancellation = new CancellationTokenSource();
        var context = new CancellationServerCallContext(cancellation.Token);

        Task call = invoke(context);
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => call.WaitAsync(TimeSpan.FromSeconds(2)));
    }

    private sealed class CancellationServerCallContext(
        CancellationToken cancellationToken) : ServerCallContext
    {
        private readonly Metadata _requestHeaders = [];
        private readonly Metadata _responseTrailers = [];
        private Status _status;
        private WriteOptions? _writeOptions;

        protected override string MethodCore => "test";
        protected override string HostCore => "localhost";
        protected override string PeerCore => "test";
        protected override DateTime DeadlineCore => DateTime.MaxValue;
        protected override Metadata RequestHeadersCore => _requestHeaders;
        protected override CancellationToken CancellationTokenCore =>
            cancellationToken;
        protected override Metadata ResponseTrailersCore => _responseTrailers;
        protected override Status StatusCore
        {
            get => _status;
            set => _status = value;
        }
        protected override WriteOptions? WriteOptionsCore
        {
            get => _writeOptions;
            set => _writeOptions = value;
        }
        protected override AuthContext AuthContextCore =>
            new("", new Dictionary<string, List<AuthProperty>>());

        protected override ContextPropagationToken CreatePropagationTokenCore(
            ContextPropagationOptions? options) =>
            throw new NotSupportedException();

        protected override Task WriteResponseHeadersAsyncCore(
            Metadata responseHeaders) => Task.CompletedTask;
    }
}

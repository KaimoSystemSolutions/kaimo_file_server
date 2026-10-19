using Kaimo_File_Server.Core.Domain;
using Kaimo_File_Server.Core.Domain.Identity;
using Kaimo_File_Server.Core.Repositories;
using Kaimo_File_Server.Core.Security;
using Kaimo_File_Server.Core.Services.DataServices;
using Kaimo_File_Server.Core.Services.File;
using Kaimo_File_Server.SmbBridge.Grpc;
using Kaimo_File_Server.SmbBridge.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Kaimo_File_Server.Tests;

public sealed class DisabledShareBridgeTests
{
    private readonly ShareDefinition _disabledShare =
        new("disabled", Path.GetTempPath(), isEnabled: false);
    private readonly User _user =
        new(Guid.NewGuid(), "Alice", "alice", "hash", "nt");

    [Fact]
    public async Task Resolver_ReturnsNullForDisabledDefinition()
    {
        var shares = DisabledShareRepository();

        var result = await shares.Object.ResolveEnabledShareAsync("disabled");

        Assert.Null(result);
    }

    [Fact]
    public async Task AuthorizationRpcs_DenyDisabledShareBeforeAclEvaluation()
    {
        var shares = DisabledShareRepository();
        var users = new Mock<IUserRepository>();
        var auth = new Mock<IAuthenticationLookup>();
        var acl = new Mock<IAclService>();
        var config = new Mock<ISmbConfigStore>();
        var userContext = new UserContext(_user, [], [], []);
        users.Setup(x => x.GetByUsernameAsync("alice")).ReturnsAsync(_user);
        auth.Setup(x => x.ResolveUserContextAsync("alice"))
            .ReturnsAsync(userContext);
        config.Setup(x => x.IsSmbEnabledAsync()).ReturnsAsync(true);
        var sut = new AuthzGrpcService(
            users.Object, shares.Object, auth.Object, acl.Object, config.Object,
            NullLogger<AuthzGrpcService>.Instance);

        var connect = await sut.AuthorizeConnect(
            new AuthorizeConnectRequest
                { Username = "alice", Share = "disabled" },
            null!);
        var open = await sut.AuthorizeOpen(
            new AuthorizeOpenRequest
            {
                Username = "alice", Share = "disabled", Path = "file.txt",
                AccessMask = 0x00000001
            },
            null!);
        var delete = await sut.AuthorizeDelete(
            new AuthorizeDeleteRequest
            {
                Username = "alice", Share = "disabled", Path = "file.txt"
            },
            null!);
        var rename = await sut.AuthorizeRename(
            new AuthorizeRenameRequest
            {
                Username = "alice", Share = "disabled",
                SourcePath = "old.txt", DestinationPath = "new.txt"
            },
            null!);

        Assert.All([connect, open, delete, rename], reply =>
        {
            Assert.False(reply.Allow);
            Assert.Contains("disabled share", reply.Reason);
        });
        acl.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task EventRpcs_DiscardDisabledShareBeforeCreatingFileService()
    {
        var shares = DisabledShareRepository();
        var auth = new Mock<IAuthenticationLookup>();
        var factory = new Mock<IFileServiceFactory>();
        var events = new Mock<ISambaLifecycleEventRepository>();
        auth.Setup(x => x.ResolveUserContextAsync("alice"))
            .ReturnsAsync(new UserContext(_user, [], [], []));
        var sut = new FileEventGrpcService(
            factory.Object, shares.Object, auth.Object, events.Object,
            NullLogger<FileEventGrpcService>.Instance);

        var close = await sut.NotifyClose(
            new NotifyCloseRequest
            {
                Username = "alice", Share = "disabled", Path = "file.txt",
                EventId = Guid.NewGuid().ToString("N")
            },
            null!);
        var mkdir = await sut.NotifyMkdir(
            new NotifyPathRequest
            {
                Username = "alice", Share = "disabled", Path = "folder",
                IsDirectory = true, EventId = Guid.NewGuid().ToString("N")
            },
            null!);
        var delete = await sut.NotifyDelete(
            new NotifyPathRequest
            {
                Username = "alice", Share = "disabled", Path = "file.txt",
                EventId = Guid.NewGuid().ToString("N")
            },
            null!);
        var rename = await sut.NotifyRename(
            new NotifyRenameRequest
            {
                Username = "alice", Share = "disabled",
                OldPath = "old.txt", NewPath = "new.txt",
                EventId = Guid.NewGuid().ToString("N")
            },
            null!);

        Assert.All([close, mkdir, delete, rename],
            reply => Assert.False(reply.Ok));
        factory.VerifyNoOtherCalls();
        events.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task SnapshotRpcs_HideDisabledShareBeforeVersionLookup()
    {
        var shares = DisabledShareRepository();
        var auth = new Mock<IAuthenticationLookup>();
        var acl = new Mock<IAclService>();
        var versions = new Mock<IFileVersionService>();
        var factory = new Mock<IFileServiceFactory>();
        auth.Setup(x => x.ResolveUserContextAsync("alice"))
            .ReturnsAsync(new UserContext(_user, [], [], []));
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Snapshots:Cache:RootPath"] =
                    Path.Combine(Path.GetTempPath(), "kaimo-disabled-share-cache")
            })
            .Build();
        var sut = new SnapshotGrpcService(
            shares.Object, auth.Object, acl.Object, versions.Object,
            factory.Object, new SnapshotCacheLeaseManager(),
            new SnapshotMaterializationLimiter(configuration), configuration,
            NullLogger<SnapshotGrpcService>.Instance);

        var enumerate = await sut.EnumerateSnapshots(
            new EnumerateSnapshotsRequest
            {
                Username = "alice", Share = "disabled", Path = ""
            },
            null!);
        var resolve = await sut.ResolveVersion(
            new ResolveVersionRequest
            {
                Username = "alice", Share = "disabled", Path = "file.txt",
                GmtToken = "@GMT-2026.07.28-12.00.00"
            },
            null!);

        Assert.Empty(enumerate.GmtTokens);
        Assert.False(resolve.Found);
        versions.VerifyNoOtherCalls();
        factory.VerifyNoOtherCalls();
        acl.VerifyNoOtherCalls();
    }

    private Mock<IShareRepository> DisabledShareRepository()
    {
        var shares = new Mock<IShareRepository>();
        shares.Setup(x => x.GetByNameAsync("disabled"))
            .ReturnsAsync(_disabledShare);
        return shares;
    }
}

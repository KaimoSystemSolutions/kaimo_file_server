using Kaimo_File_Server.Core.Domain;
using Kaimo_File_Server.Core.Domain.Identity;
using Kaimo_File_Server.Core.Repositories;
using Kaimo_File_Server.Core.Security;
using Kaimo_File_Server.Core.Services;
using Kaimo_File_Server.Core.Services.DataServices;
using Kaimo_File_Server.SmbBridge.Grpc;
using Kaimo_File_Server.SmbBridge.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Kaimo_File_Server.Tests;

public sealed class AuthzGrpcServiceRenameTests : IDisposable
{
    private readonly Mock<IUserRepository> _users = new();
    private readonly Mock<IShareRepository> _shares = new();
    private readonly Mock<IAuthenticationLookup> _auth = new();
    private readonly Mock<IAclService> _acl = new();
    private readonly Mock<ISmbConfigStore> _config = new();
    private readonly string _tempRoot;
    private readonly ShareDefinition _share;
    private readonly UserContext _user;
    private readonly AuthzGrpcService _sut;
    private readonly InMemoryCloudSyncOperationCoordinator _operations =
        new(TimeProvider.System);

    public AuthzGrpcServiceRenameTests()
    {
        _tempRoot = Path.Combine(
            Path.GetTempPath(), "kaimo-authz-rename-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(_tempRoot, "docs"));
        Directory.CreateDirectory(Path.Combine(_tempRoot, "archive"));
        File.WriteAllText(Path.Combine(_tempRoot, "docs", "report.txt"), "source");

        _share = new ShareDefinition("share", _tempRoot, isEnabled: true);
        var user = new User(Guid.NewGuid(), "Alice", "alice", "hash", "nt");
        _user = new UserContext(user, [], [], []);
        _auth.Setup(a => a.ResolveUserContextAsync("alice")).ReturnsAsync(_user);
        _shares.Setup(s => s.GetByNameAsync("share")).ReturnsAsync(_share);
        _acl.Setup(a => a.HasAccessAsync(
                _user, _share.Id, It.IsAny<string>(), true,
                FilePermission.TraverseExecute))
            .ReturnsAsync(true);

        _sut = new AuthzGrpcService(
            _users.Object,
            _shares.Object,
            _auth.Object,
            _acl.Object,
            _config.Object,
            NullLogger<AuthzGrpcService>.Instance,
            _operations);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempRoot))
            Directory.Delete(_tempRoot, recursive: true);
    }

    [Fact]
    public async Task AuthorizeRename_FileMove_RequiresSourceDeleteAndDestinationParentCreate()
    {
        Allow("docs/report.txt", false, FilePermission.Delete);
        Allow("archive", true, FilePermission.CreateWriteData);

        var reply = await AuthorizeAsync(
            "docs/report.txt", "archive/report.txt", sourceIsDirectory: false);

        Assert.True(reply.Allow);
    }

    [Fact]
    public async Task AuthorizeRename_DirectoryMove_RequiresCreateAppendData()
    {
        Directory.CreateDirectory(Path.Combine(_tempRoot, "docs", "folder"));
        Allow("docs/folder", true, FilePermission.Delete);
        Allow("archive", true, FilePermission.CreateAppendData);

        var reply = await AuthorizeAsync(
            "docs/folder", "archive/folder", sourceIsDirectory: true);

        Assert.True(reply.Allow);
        _acl.Verify(a => a.HasAccessAsync(
            _user, _share.Id, "archive", true,
            FilePermission.CreateWriteData), Times.Never);
    }

    [Fact]
    public async Task AuthorizeRename_SourceParentDeleteSubItems_IsAccepted()
    {
        Allow("docs", true, FilePermission.DeleteSubItems);
        Allow("archive", true, FilePermission.CreateWriteData);

        var reply = await AuthorizeAsync(
            "docs/report.txt", "archive/report.txt", sourceIsDirectory: false);

        Assert.True(reply.Allow);
    }

    [Fact]
    public async Task AuthorizeRename_SourceDeleteDenied_IsDenied()
    {
        Allow("archive", true, FilePermission.CreateWriteData);

        var reply = await AuthorizeAsync(
            "docs/report.txt", "archive/report.txt", sourceIsDirectory: false);

        Assert.False(reply.Allow);
        _acl.Verify(a => a.HasAccessAsync(
            _user, _share.Id, "archive", true,
            FilePermission.CreateWriteData), Times.Never);
    }

    [Fact]
    public async Task AuthorizeRename_ExistingTarget_RequiresReplacementDelete()
    {
        File.WriteAllText(Path.Combine(_tempRoot, "archive", "report.txt"), "target");
        Allow("docs/report.txt", false, FilePermission.Delete);
        Allow("archive", true, FilePermission.CreateWriteData);

        var reply = await AuthorizeAsync(
            "docs/report.txt", "archive/report.txt", sourceIsDirectory: false,
            destinationExists: true, replaceIntent: true);

        Assert.False(reply.Allow);
    }

    [Fact]
    public async Task AuthorizeRename_ExistingTargetDelete_IsAccepted()
    {
        File.WriteAllText(Path.Combine(_tempRoot, "archive", "report.txt"), "target");
        Allow("docs/report.txt", false, FilePermission.Delete);
        Allow("archive", true, FilePermission.CreateWriteData);
        Allow("archive/report.txt", false, FilePermission.Delete);

        var reply = await AuthorizeAsync(
            "docs/report.txt", "archive/report.txt", sourceIsDirectory: false,
            destinationExists: true, replaceIntent: true);

        Assert.True(reply.Allow);
    }

    [Fact]
    public async Task AuthorizeRename_ReplacementParentDeleteSubItems_IsAccepted()
    {
        File.WriteAllText(Path.Combine(_tempRoot, "archive", "report.txt"), "target");
        Allow("docs/report.txt", false, FilePermission.Delete);
        Allow("archive", true, FilePermission.CreateWriteData);
        Allow("archive", true, FilePermission.DeleteSubItems);

        var reply = await AuthorizeAsync(
            "docs/report.txt", "archive/report.txt", sourceIsDirectory: false,
            destinationExists: true, replaceIntent: true);

        Assert.True(reply.Allow);
    }

    [Fact]
    public async Task AuthorizeRename_MissingTarget_DoesNotRequireReplacementDelete()
    {
        Allow("docs/report.txt", false, FilePermission.Delete);
        Allow("archive", true, FilePermission.CreateWriteData);

        var reply = await AuthorizeAsync(
            "docs/report.txt", "archive/report.txt", sourceIsDirectory: false);

        Assert.True(reply.Allow);
        _acl.Verify(a => a.HasAccessAsync(
            _user, _share.Id, "archive/report.txt", false,
            FilePermission.Delete), Times.Never);
    }

    [Fact]
    public async Task AuthorizeRename_DestinationParentCreateDenied_IsDenied()
    {
        Allow("docs/report.txt", false, FilePermission.Delete);

        var reply = await AuthorizeAsync(
            "docs/report.txt", "archive/report.txt", sourceIsDirectory: false);

        Assert.False(reply.Allow);
    }

    [Fact]
    public async Task AuthorizeRename_StaleDestinationState_IsDeniedBeforeAclChecks()
    {
        File.WriteAllText(Path.Combine(_tempRoot, "archive", "report.txt"), "target");

        var reply = await AuthorizeAsync(
            "docs/report.txt", "archive/report.txt", sourceIsDirectory: false,
            destinationExists: false, replaceIntent: false);

        Assert.False(reply.Allow);
        _acl.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task AuthorizeRename_StaleSourceType_IsDeniedBeforeAclChecks()
    {
        var reply = await AuthorizeAsync(
            "docs/report.txt", "archive/report.txt", sourceIsDirectory: true);

        Assert.False(reply.Allow);
        _acl.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task AuthorizeRename_StaleDestinationType_IsDeniedBeforeAclChecks()
    {
        Directory.CreateDirectory(Path.Combine(_tempRoot, "archive", "folder"));

        var reply = await AuthorizeAsync(
            "docs/report.txt", "archive/folder", sourceIsDirectory: false,
            destinationExists: true, destinationIsDirectory: false,
            replaceIntent: true);

        Assert.False(reply.Allow);
        _acl.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task AuthorizeRename_MismatchedReplacementIntent_IsDenied()
    {
        File.WriteAllText(Path.Combine(_tempRoot, "archive", "report.txt"), "target");

        var reply = await AuthorizeAsync(
            "docs/report.txt", "archive/report.txt", sourceIsDirectory: false,
            destinationExists: true, replaceIntent: false);

        Assert.False(reply.Allow);
        _acl.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task AuthorizeRename_TraversalPath_IsDeniedWithoutAclChecks()
    {
        var reply = await AuthorizeAsync(
            "docs/report.txt", "../outside.txt", sourceIsDirectory: false);

        Assert.False(reply.Allow);
        _acl.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task AuthorizeRename_OverlappingCloudSync_IsDeniedBeforeNativeRename()
    {
        Allow("docs/report.txt", false, FilePermission.Delete);
        Allow("archive", true, FilePermission.CreateWriteData);
        var syncLease = await _operations.TryBeginSyncAsync(
            _share.Id, "docs");
        Assert.NotNull(syncLease);
        await using var activeSync = syncLease;

        var reply = await AuthorizeAsync(
            "docs/report.txt", "archive/report.txt", sourceIsDirectory: false);

        Assert.False(reply.Allow);
        Assert.Contains("cloud sync", reply.Reason);
    }

    [Fact]
    public async Task AuthorizeRename_ReservedDestination_IsDeniedBeforeAclChecks()
    {
        var reply = await AuthorizeAsync(
            "docs/report.txt", ".RECYCLE_BIN/report.txt",
            sourceIsDirectory: false);

        Assert.False(reply.Allow);
        Assert.Contains("reserved Kaimo namespace", reply.Reason);
        _acl.VerifyNoOtherCalls();
    }

    private Task<AuthorizeReply> AuthorizeAsync(
        string sourcePath,
        string destinationPath,
        bool sourceIsDirectory,
        bool destinationExists = false,
        bool destinationIsDirectory = false,
        bool replaceIntent = false) =>
        _sut.AuthorizeRename(
            new AuthorizeRenameRequest
            {
                Username = "alice",
                Share = "share",
                SourcePath = sourcePath,
                DestinationPath = destinationPath,
                SourceIsDirectory = sourceIsDirectory,
                DestinationExists = destinationExists,
                DestinationIsDirectory = destinationIsDirectory,
                ReplaceIntent = replaceIntent
            },
            null!);

    private void Allow(string path, bool isDirectory, FilePermission permission) =>
        _acl.Setup(a => a.HasAccessAsync(
                _user, _share.Id, path, isDirectory, permission))
            .ReturnsAsync(true);
}

using Kaimo_File_Server.Core.Domain;
using Kaimo_File_Server.Core.Domain.Identity;
using Kaimo_File_Server.Core.Repositories;
using Kaimo_File_Server.Core.Security;
using Kaimo_File_Server.Core.Services.DataServices;
using Kaimo_File_Server.SmbBridge.Grpc;
using Kaimo_File_Server.SmbBridge.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Kaimo_File_Server.Tests;

public sealed class AuthzGrpcServiceDeleteTests : IDisposable
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

    public AuthzGrpcServiceDeleteTests()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(), "kaimo-authz-delete-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(_tempRoot, "docs"));
        File.WriteAllText(Path.Combine(_tempRoot, "docs", "report.txt"), "test");
        _share = new ShareDefinition("share", _tempRoot, isEnabled: true);

        var user = new User(Guid.NewGuid(), "Alice", "alice", "hash", "nt");
        _user = new UserContext(user, [], [], []);
        _auth.Setup(a => a.ResolveUserContextAsync("alice")).ReturnsAsync(_user);
        _shares.Setup(s => s.GetByNameAsync("share")).ReturnsAsync(_share);

        _sut = new AuthzGrpcService(
            _users.Object,
            _shares.Object,
            _auth.Object,
            _acl.Object,
            _config.Object,
            NullLogger<AuthzGrpcService>.Instance);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempRoot))
            Directory.Delete(_tempRoot, recursive: true);
    }

    [Fact]
    public async Task AuthorizeDelete_ReadOnlyUser_IsDenied()
    {
        Deny(FilePermission.Delete);
        Deny(FilePermission.DeleteSubItems);

        var reply = await AuthorizeAsync("docs/report.txt", isDirectory: false);

        Assert.False(reply.Allow);
    }

    [Fact]
    public async Task AuthorizeDelete_TargetHasDelete_IsAllowed()
    {
        Allow("docs/report.txt", isDirectory: false, FilePermission.Delete);

        var reply = await AuthorizeAsync("docs/report.txt", isDirectory: false);

        Assert.True(reply.Allow);
        _acl.Verify(a => a.HasAccessAsync(
            _user, _share.Id, "docs", true, FilePermission.DeleteSubItems), Times.Never);
    }

    [Fact]
    public async Task AuthorizeDelete_ParentHasDeleteSubItems_IsAllowed()
    {
        Deny(FilePermission.Delete);
        Allow("docs", isDirectory: true, FilePermission.DeleteSubItems);

        var reply = await AuthorizeAsync("docs/report.txt", isDirectory: false);

        Assert.True(reply.Allow);
    }

    [Fact]
    public async Task AuthorizeDelete_TraversalPath_IsDeniedWithoutAclLookup()
    {
        var reply = await AuthorizeAsync("../outside.txt", isDirectory: false);

        Assert.False(reply.Allow);
        _acl.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task AuthorizeOpen_DeleteOnlyWithReadPermission_IsDenied()
    {
        Allow("", isDirectory: true, FilePermission.TraverseExecute);
        Allow("docs", isDirectory: true, FilePermission.TraverseExecute);
        Allow("docs/report.txt", isDirectory: false, FilePermission.ReadAttributes);
        Deny(FilePermission.Delete);
        Deny(FilePermission.DeleteSubItems);

        var reply = await _sut.AuthorizeOpen(
            new AuthorizeOpenRequest
            {
                Username = "alice",
                Share = "share",
                Path = "docs/report.txt",
                AccessMask = 0x00010000
            },
            null!);

        Assert.False(reply.Allow);
    }

    private Task<AuthorizeReply> AuthorizeAsync(string path, bool isDirectory) =>
        _sut.AuthorizeDelete(
            new AuthorizeDeleteRequest
            {
                Username = "alice",
                Share = "share",
                Path = path,
                IsDirectory = isDirectory
            },
            null!);

    private void Allow(string path, bool isDirectory, FilePermission permission) =>
        _acl.Setup(a => a.HasAccessAsync(_user, _share.Id, path, isDirectory, permission))
            .ReturnsAsync(true);

    private void Deny(FilePermission permission) =>
        _acl.Setup(a => a.HasAccessAsync(
                _user, _share.Id, It.IsAny<string>(), It.IsAny<bool>(), permission))
            .ReturnsAsync(false);
}

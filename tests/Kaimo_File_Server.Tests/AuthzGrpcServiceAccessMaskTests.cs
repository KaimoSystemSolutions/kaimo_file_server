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

public sealed class AuthzGrpcServiceAccessMaskTests : IDisposable
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

    public AuthzGrpcServiceAccessMaskTests()
    {
        _tempRoot = Path.Combine(
            Path.GetTempPath(), "kaimo-authz-mask-" + Guid.NewGuid().ToString("N"));
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

    public static IEnumerable<object[]> SpecificAccessMappings()
    {
        yield return [0x00000001u, FilePermission.ListReadData];
        yield return [0x00000002u, FilePermission.CreateWriteData];
        yield return [0x00000004u, FilePermission.CreateAppendData];
        yield return [0x00000008u, FilePermission.ReadExtAttributes];
        yield return [0x00000010u, FilePermission.WriteExtAttributes];
        yield return [0x00000020u, FilePermission.TraverseExecute];
        yield return [0x00000080u, FilePermission.ReadAttributes];
        yield return [0x00000100u, FilePermission.WriteAttributes];
        yield return [0x00020000u, FilePermission.ReadPermissions];
        yield return [0x00040000u, FilePermission.ChangePermissions];
        yield return [0x00080000u, FilePermission.TakeOwnership];
    }

    [Theory]
    [MemberData(nameof(SpecificAccessMappings))]
    public async Task AuthorizeOpen_EachSpecificBit_RequiresMappedPermission(
        uint accessMask, FilePermission expectedPermission)
    {
        AllowTraversal("docs/report.txt");
        Allow("docs/report.txt", false, FilePermission.ReadAttributes);
        Allow("docs/report.txt", false, expectedPermission);

        var reply = await AuthorizeAsync(accessMask);

        Assert.True(reply.Allow);
        Assert.Equal(accessMask | 0x00000080u, reply.GrantedAccessMask);
        _acl.Verify(a => a.HasAccessAsync(
            _user, _share.Id, "docs/report.txt", false, expectedPermission),
            Times.AtLeastOnce);
    }

    [Fact]
    public async Task AuthorizeOpen_AppendOnly_DoesNotRequireFullWrite()
    {
        AllowTraversal("docs/report.txt");
        Allow("docs/report.txt", false, FilePermission.ReadAttributes);
        Allow("docs/report.txt", false, FilePermission.CreateAppendData);

        var reply = await AuthorizeAsync(0x00000004);

        Assert.True(reply.Allow);
        _acl.Verify(a => a.HasAccessAsync(
            _user, _share.Id, "docs/report.txt", false,
            FilePermission.CreateWriteData), Times.Never);
    }

    [Fact]
    public async Task AuthorizeOpen_Delete_UsesTargetOrParentDeleteSemantics()
    {
        AllowTraversal("docs/report.txt");
        Allow("docs/report.txt", false, FilePermission.ReadAttributes);
        Allow("docs", true, FilePermission.DeleteSubItems);

        var reply = await AuthorizeAsync(0x00010000);

        Assert.True(reply.Allow);
        Assert.Equal(0x00010080u, reply.GrantedAccessMask);
    }

    [Fact]
    public async Task AuthorizeOpen_DeleteChild_RequiresDirectoryDeleteSubItems()
    {
        AllowTraversal("docs");
        Allow("docs", true, FilePermission.ReadAttributes);
        Allow("docs", true, FilePermission.DeleteSubItems);

        var reply = await AuthorizeAsync(0x00000040, path: "docs");

        Assert.True(reply.Allow);
        Assert.Equal(0x000000C0u, reply.GrantedAccessMask);
    }

    [Fact]
    public async Task AuthorizeOpen_DeleteChildOnFile_IsDenied()
    {
        AllowTraversal("docs/report.txt");
        Allow("docs/report.txt", false, FilePermission.ReadAttributes);

        var reply = await AuthorizeAsync(0x00000040);

        Assert.False(reply.Allow);
        Assert.Contains("only for directories", reply.Reason);
    }

    [Fact]
    public async Task AuthorizeOpen_GenericWrite_IsExpandedExactly()
    {
        AllowTraversal("docs/report.txt");
        foreach (var permission in new[]
                 {
                     FilePermission.CreateWriteData,
                     FilePermission.CreateAppendData,
                     FilePermission.WriteAttributes,
                     FilePermission.WriteExtAttributes,
                     FilePermission.ReadPermissions,
                     FilePermission.ReadAttributes
                 })
            Allow("docs/report.txt", false, permission);

        var reply = await AuthorizeAsync(0x40000000);

        Assert.True(reply.Allow);
        Assert.Equal(0x00120196u, reply.GrantedAccessMask);
    }

    [Fact]
    public async Task AuthorizeOpen_MaximumAllowed_IsAttenuatedToKaimoRights()
    {
        AllowTraversal("docs/report.txt");
        Allow("docs/report.txt", false, FilePermission.ListReadData);
        Allow("docs/report.txt", false, FilePermission.ReadAttributes);

        var reply = await AuthorizeAsync(0x02000000);

        Assert.True(reply.Allow);
        Assert.Equal(0x00100081u, reply.GrantedAccessMask);
        Assert.Equal(0u, reply.GrantedAccessMask & 0x00000116u);
    }

    [Fact]
    public async Task AuthorizeOpen_MaximumAllowedWithoutReadAttributes_IsDenied()
    {
        AllowTraversal("docs/report.txt");
        Allow("docs/report.txt", false, FilePermission.ListReadData);

        var reply = await AuthorizeAsync(0x02000000);

        Assert.False(reply.Allow);
        Assert.Contains("FILE_READ_ATTRIBUTES", reply.Reason);
    }

    [Fact]
    public async Task AuthorizeOpen_DirectoryCreate_UsesParentCreateAppendData()
    {
        AllowTraversal("docs/new-folder");
        Allow("docs", true, FilePermission.CreateAppendData);
        Allow("docs/new-folder", true, FilePermission.CreateAppendData);
        Allow("docs/new-folder", true, FilePermission.ReadAttributes);

        var reply = await AuthorizeAsync(
            0x00000004, "docs/new-folder", wantsCreate: true,
            createDirectory: true);

        Assert.True(reply.Allow);
        _acl.Verify(a => a.HasAccessAsync(
            _user, _share.Id, "docs", true,
            FilePermission.CreateAppendData), Times.Once);
        _acl.Verify(a => a.HasAccessAsync(
            _user, _share.Id, "docs", true,
            FilePermission.CreateWriteData), Times.Never);
    }

    [Fact]
    public async Task AuthorizeOpen_FileCreate_UsesParentCreateWriteData()
    {
        AllowTraversal("docs/new-file.txt");
        Allow("docs", true, FilePermission.CreateWriteData);
        Allow("docs/new-file.txt", false, FilePermission.CreateWriteData);
        Allow("docs/new-file.txt", false, FilePermission.ReadAttributes);

        var reply = await AuthorizeAsync(
            0x00000002, "docs/new-file.txt", wantsCreate: true);

        Assert.True(reply.Allow);
        _acl.Verify(a => a.HasAccessAsync(
            _user, _share.Id, "docs", true,
            FilePermission.CreateWriteData), Times.Once);
    }

    [Theory]
    [InlineData(".RECYCLE_BIN/new-file.txt", false, 0x00000002u)]
    [InlineData(".RECYCLE_BIN/new-folder", true, 0x00000004u)]
    public async Task AuthorizeOpen_RecycleNamespaceCreate_IsDeniedBeforeAcl(
        string path, bool createDirectory, uint accessMask)
    {
        var reply = await AuthorizeAsync(
            accessMask, path, wantsCreate: true,
            createDirectory: createDirectory);

        Assert.False(reply.Allow);
        Assert.Contains("reserved Kaimo namespace", reply.Reason);
        _acl.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task AuthorizeOpen_RecycleNamespaceExplicitWrite_IsDeniedBeforeAcl()
    {
        Directory.CreateDirectory(Path.Combine(_tempRoot, ".RECYCLE_BIN"));
        File.WriteAllText(
            Path.Combine(_tempRoot, ".RECYCLE_BIN", "existing.txt"), "test");

        var reply = await AuthorizeAsync(
            0x00000002u, ".RECYCLE_BIN/existing.txt");

        Assert.False(reply.Allow);
        Assert.Contains("reserved Kaimo namespace", reply.Reason);
        _acl.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task AuthorizeOpen_DirectoryListing_DoesNotInventReadAttributes()
    {
        AllowTraversal("docs/report.txt");
        Allow("docs/report.txt", false, FilePermission.ListReadData);

        var reply = await AuthorizeAsync(
            0x00000001, directoryListing: true);

        Assert.True(reply.Allow);
        Assert.Equal(0x00000001u, reply.GrantedAccessMask);
        _acl.Verify(a => a.HasAccessAsync(
            _user, _share.Id, "docs/report.txt", false,
            FilePermission.ReadAttributes), Times.Never);
    }

    [Fact]
    public async Task AuthorizeOpen_TraversalDenied_FailsBeforeTargetPermission()
    {
        Allow("", true, FilePermission.TraverseExecute);
        Allow("docs/report.txt", false, FilePermission.ListReadData);
        Allow("docs/report.txt", false, FilePermission.ReadAttributes);

        var reply = await AuthorizeAsync(0x00000001);

        Assert.False(reply.Allow);
        Assert.Contains("ancestor 'docs'", reply.Reason);
        _acl.Verify(a => a.HasAccessAsync(
            _user, _share.Id, "docs/report.txt", false,
            FilePermission.ListReadData), Times.Never);
    }

    [Theory]
    [InlineData(0x01000000u, "ACCESS_SYSTEM_SECURITY")]
    [InlineData(0x00000200u, "unsupported access-mask bits")]
    public async Task AuthorizeOpen_UnsupportedSecurityBits_AreDenied(
        uint accessMask, string expectedReason)
    {
        var reply = await AuthorizeAsync(accessMask);

        Assert.False(reply.Allow);
        Assert.Contains(expectedReason, reply.Reason);
        _acl.VerifyNoOtherCalls();
    }

    [Theory]
    [InlineData("/docs/report.txt")]
    [InlineData(@"\docs\report.txt")]
    [InlineData("C:/docs/report.txt")]
    [InlineData("docs/../report.txt")]
    [InlineData(".kaimo-close-captures/event.cap")]
    public async Task AuthorizeOpen_RejectsNonClientRelativePathBeforeAcl(
        string path)
    {
        var reply = await AuthorizeAsync(0x00000001, path);

        Assert.False(reply.Allow);
        Assert.Contains("invalid open path", reply.Reason);
        _acl.VerifyNoOtherCalls();
    }

    private Task<AuthorizeReply> AuthorizeAsync(
        uint accessMask,
        string path = "docs/report.txt",
        bool wantsCreate = false,
        bool createDirectory = false,
        bool directoryListing = false) =>
        _sut.AuthorizeOpen(
            new AuthorizeOpenRequest
            {
                Username = "alice",
                Share = "share",
                Path = path,
                AccessMask = accessMask,
                WantsCreate = wantsCreate,
                CreateDirectory = createDirectory,
                DirectoryListing = directoryListing
            },
            null!);

    private void AllowTraversal(string path)
    {
        var segments = path.Split('/');
        Allow("", true, FilePermission.TraverseExecute);
        string current = "";
        for (int i = 0; i < segments.Length - 1; i++)
        {
            current = string.IsNullOrEmpty(current)
                ? segments[i]
                : $"{current}/{segments[i]}";
            Allow(current, true, FilePermission.TraverseExecute);
        }
    }

    private void Allow(string path, bool isDirectory, FilePermission permission) =>
        _acl.Setup(a => a.HasAccessAsync(
                _user, _share.Id, path, isDirectory, permission))
            .ReturnsAsync(true);

    public void Dispose()
    {
        if (Directory.Exists(_tempRoot))
            Directory.Delete(_tempRoot, recursive: true);
    }
}

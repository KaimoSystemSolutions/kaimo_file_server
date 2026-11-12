using Kaimo_File_Server.Core.Domain;
using Kaimo_File_Server.Core.Domain.Identity;
using Kaimo_File_Server.Core.Repositories.Identity;
using Kaimo_File_Server.Core.Repositories.Shares;
using Kaimo_File_Server.Core.Security;
using Kaimo_File_Server.Core.Security.Acl;
using Kaimo_File_Server.Core.Security.Authentication;
using Kaimo_File_Server.Core.Services.DataServices;
using Kaimo_File_Server.Core.Services.File;
using Kaimo_File_Server.SmbBridge.Grpc;
using Kaimo_File_Server.SmbBridge.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Kaimo_File_Server.SmbBridge.Tests.Services;

/// <summary>SMB creates and renames must not shadow a virtual-share mount point.</summary>
public sealed class AuthzGrpcServiceMountTests : IDisposable
{
    private const uint GenericWrite = 0x00000002u;
    private readonly Mock<IShareRepository> _shares = new();
    private readonly Mock<IAuthenticationLookup> _auth = new();
    private readonly Mock<IAclService> _acl = new();
    private readonly Mock<ICloudAccessMountGuard> _guard = new();
    private readonly string _tempRoot;
    private readonly ShareDefinition _share;

    public AuthzGrpcServiceMountTests()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(), "kaimo-authz-mount-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(_tempRoot, "Team", "Local"));
        _share = new ShareDefinition("share", _tempRoot, isEnabled: true);
        var user = new UserContext(new User(Guid.NewGuid(), "Alice", "alice", "hash", "nt"), [], [], []);
        _auth.Setup(a => a.ResolveUserContextAsync("alice")).ReturnsAsync(user);
        _shares.Setup(s => s.GetByNameAsync("share")).ReturnsAsync(_share);
        _acl.Setup(a => a.HasAccessAsync(user, _share.Id, It.IsAny<string>(), It.IsAny<bool>(), It.IsAny<FilePermission>()))
            .ReturnsAsync(true);
        _guard.Setup(g => g.IsReservedAsync(_share.Id, It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((Guid _, string p, CancellationToken _) =>
                p.Equals("Team/Cloud", StringComparison.OrdinalIgnoreCase)
                || p.StartsWith("Team/Cloud/", StringComparison.OrdinalIgnoreCase));
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempRoot))
            Directory.Delete(_tempRoot, recursive: true);
    }

    private AuthzGrpcService Sut(bool withGuard = true) => new(
        new Mock<IUserRepository>().Object, _shares.Object, _auth.Object, _acl.Object,
        new Mock<ISmbConfigStore>().Object, NullLogger<AuthzGrpcService>.Instance,
        mountGuard: withGuard ? _guard.Object : null);

    private static AuthorizeOpenRequest Create(string path, bool directory = true) => new()
    {
        Username = "alice", Share = "share", Path = path, AccessMask = GenericWrite,
        WantsCreate = true, CreateDirectory = directory
    };

    private static AuthorizeRenameRequest Rename(string source, string destination) => new()
    {
        Username = "alice", Share = "share", SourcePath = source, DestinationPath = destination,
        SourceIsDirectory = true
    };

    [Theory]
    [InlineData("Team/Cloud")]
    [InlineData("team/cloud")]
    [InlineData("Team/Cloud/sub")]
    public async Task CreateOnMount_IsDenied(string path)
        => Assert.False((await Sut().AuthorizeOpen(Create(path), null!)).Allow);

    [Fact]
    public async Task CreateSibling_IsAllowed()
        => Assert.True((await Sut().AuthorizeOpen(Create("Team/Cloud2"), null!)).Allow);

    [Fact]
    public async Task RenameOntoMount_IsDenied()
        => Assert.False((await Sut().AuthorizeRename(Rename("Team/Local", "Team/Cloud"), null!)).Allow);

    [Fact]
    public async Task RenameElsewhere_IsAllowed()
        => Assert.True((await Sut().AuthorizeRename(Rename("Team/Local", "Team/Other"), null!)).Allow);

    [Fact]
    public async Task WithoutGuard_CreateOnMountNameIsAllowed()
        => Assert.True((await Sut(withGuard: false).AuthorizeOpen(Create("Team/Cloud"), null!)).Allow);
}

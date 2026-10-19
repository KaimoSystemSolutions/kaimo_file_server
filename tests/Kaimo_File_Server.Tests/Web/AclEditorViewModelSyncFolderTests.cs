using System.Security.Claims;
using System.Threading;
using Kaimo_File_Server.Core.Domain;
using Kaimo_File_Server.Core.Domain.Identity;
using Kaimo_File_Server.Core.Language;
using Kaimo_File_Server.Core.Repositories;
using Kaimo_File_Server.Core.Security;
using Kaimo_File_Server.Core.Services;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Kaimo_File_Server.Tests;

/// <summary>
/// Pins the ACL editor's behavior for items inside a cloud/external sync folder.
/// The sync reconciles by path (no stable file identity), so a rename it performs
/// silently drops a per-item ACL. The editor therefore warns for any item below a
/// sync root, while leaving the root itself unflagged. The warning is informational
/// only — writes below the root stay allowed.
/// </summary>
public class AclEditorViewModelSyncFolderTests
{
    private readonly Mock<IAclRepository> _aclRepo = new();
    private readonly Mock<IFileMetadataRepository> _metaRepo = new();
    private readonly Mock<IUserRepository> _userRepo = new();
    private readonly Mock<IGroupRepository> _groupRepo = new();
    private readonly Mock<IRoleRepository> _roleRepo = new();
    private readonly Mock<IShareRepository> _shareRepo = new();
    private readonly Mock<ISyncDefinitionRepository> _syncDefinitions = new();
    private readonly Mock<IDepartmentRepository> _departmentRepo = new();
    private readonly Mock<IDepartmentPermissionService> _deptPermissions = new();
    private readonly Mock<IManagementAuthService> _mgmtAuth = new();
    private readonly Mock<IUserContextFactory> _userContextFactory = new();
    private readonly Mock<AuthenticationStateProvider> _authState = new();

    private readonly Guid _shareId = Guid.NewGuid();
    private readonly Guid _principalId = Guid.NewGuid();
    private readonly AclEditorViewModel _sut;

    public AclEditorViewModelSyncFolderTests()
    {
        var identity = new ClaimsIdentity(
            new[] { new Claim(ClaimTypes.Name, "alice") }, authenticationType: "test");
        _authState
            .Setup(a => a.GetAuthenticationStateAsync())
            .ReturnsAsync(new AuthenticationState(new ClaimsPrincipal(identity)));

        var user = new User(Guid.NewGuid(), "Alice", "alice", "hash", "nt");
        _userContextFactory
            .Setup(f => f.CreateByUsernameAsync("alice"))
            .ReturnsAsync(new UserContext(user, [], [], []));

        _mgmtAuth
            .Setup(m => m.CanManageShareAsync(
                It.IsAny<UserContext>(), _shareId, ManagementPermission.ManageShareAcls))
            .ReturnsAsync(true);

        _metaRepo
            .Setup(m => m.GetOrCreateAsync(
                It.IsAny<string>(), It.IsAny<bool>(), It.IsAny<Guid>(), _shareId))
            .ReturnsAsync((string p, bool d, Guid _, Guid _) =>
                new FileMetadata { Id = Guid.NewGuid(), Path = p, Name = p, IsDirectory = d, Acl = [] });
        _aclRepo
            .Setup(a => a.GetByFileMetadataIdAsync(It.IsAny<Guid>()))
            .ReturnsAsync(new List<AccessEntry>());
        _aclRepo
            .Setup(a => a.GetAclsForPathsAsync(It.IsAny<Guid>(), It.IsAny<List<string>>()))
            .ReturnsAsync(new List<(string, bool, List<AccessEntry>)>());
        _aclRepo
            .Setup(a => a.AddAsync(It.IsAny<AccessEntry>()))
            .ReturnsAsync((AccessEntry e) => e);
        _aclRepo.Setup(a => a.DeleteAsync(It.IsAny<Guid>())).Returns(Task.CompletedTask);
        _userRepo.Setup(u => u.GetAllAsync()).ReturnsAsync(new List<User>());
        _groupRepo.Setup(g => g.GetAllAsync()).ReturnsAsync(new List<Group>());
        _roleRepo.Setup(r => r.GetAllAsync()).ReturnsAsync(new List<Role>());
        // Null share → no department default lookup; keeps LoadAsync clean.
        _shareRepo.Setup(s => s.GetByIdAsync(It.IsAny<Guid>())).ReturnsAsync((ShareDefinition?)null);

        _sut = new AclEditorViewModel(
            _aclRepo.Object, _metaRepo.Object, _userRepo.Object,
            _groupRepo.Object, _roleRepo.Object, _shareRepo.Object,
            _syncDefinitions.Object,
            _departmentRepo.Object, _deptPermissions.Object, _mgmtAuth.Object,
            _userContextFactory.Object, _authState.Object,
            NullLogger<AclEditorViewModel>.Instance);
    }

    /// <summary>Makes the share host a single enabled sync rooted at <paramref name="root"/>.</summary>
    private void WithSyncRoot(string root) =>
        _syncDefinitions
            .Setup(s => s.GetEnabledByShareAsync(_shareId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<SyncDefinition>
            {
                new()
                {
                    LocalShareId = _shareId,
                    LocalPath = root,
                    Enabled = true,
                    AdvancedSettings = new CloudSyncAdvancedSettings()
                }
            });

    private void NoSyncFolders() =>
        _syncDefinitions
            .Setup(s => s.GetEnabledByShareAsync(_shareId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<SyncDefinition>());

    private void StageNewEntry()
    {
        _sut.NewPrincipalId = _principalId;
        _sut.NewPermissions = FilePermission.ListReadData;
        _sut.NewEntryType = AclEntryType.Allow;
    }

    [Fact]
    public async Task Load_InsideSyncRoot_WarnsButAllowsWrites()
    {
        WithSyncRoot("cloud");

        await _sut.LoadAsync("cloud/reports/q3.xlsx", _shareId, isDirectory: false);

        Assert.Equal(Resources.Web_Acl_SyncFolderWarning, _sut.WarningMessage);

        StageNewEntry();
        bool added = await _sut.AddEntryAsync();

        Assert.True(added);
        _aclRepo.Verify(a => a.AddAsync(It.IsAny<AccessEntry>()), Times.Once);
    }

    [Fact]
    public async Task Load_OnSyncRootItself_DoesNotWarn()
    {
        WithSyncRoot("cloud");

        await _sut.LoadAsync("cloud", _shareId, isDirectory: true);

        Assert.Null(_sut.WarningMessage);

        StageNewEntry();
        bool added = await _sut.AddEntryAsync();
        Assert.True(added);
    }

    [Fact]
    public async Task Load_OutsideAnySyncFolder_NeitherWarnsNorBlocks()
    {
        NoSyncFolders();

        await _sut.LoadAsync("private/notes.txt", _shareId, isDirectory: false);

        Assert.Null(_sut.WarningMessage);

        StageNewEntry();
        bool added = await _sut.AddEntryAsync();
        Assert.True(added);
    }
}

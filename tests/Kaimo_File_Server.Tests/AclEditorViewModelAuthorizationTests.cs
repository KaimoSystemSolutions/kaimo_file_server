using System.Security.Claims;
using System.Threading;
using Kaimo_File_Server.Core.Domain;
using Kaimo_File_Server.Core.Domain.Identity;
using Kaimo_File_Server.Core.Repositories;
using Kaimo_File_Server.Core.Security;
using Kaimo_File_Server.Core.Services;
using Kaimo_File_Server.Core.Language;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Kaimo_File_Server.Tests;

/// <summary>
/// Guards the server-side authorization of <see cref="AclEditorViewModel"/>. Before the fix the
/// editor performed NO permission check at the data layer — the only gate was whether the UI
/// rendered the buttons. These tests pin that every load and every mutation is refused unless the
/// current user holds <see cref="ManagementPermission.ManageShareAcls"/> on the target share,
/// and that a refusal never touches the ACL repository.
/// </summary>
public class AclEditorViewModelAuthorizationTests
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
    private readonly AclEditorViewModel _sut;

    public AclEditorViewModelAuthorizationTests()
    {
        // Authenticated as "alice".
        var identity = new ClaimsIdentity(
            new[] { new Claim(ClaimTypes.Name, "alice") }, authenticationType: "test");
        _authState
            .Setup(a => a.GetAuthenticationStateAsync())
            .ReturnsAsync(new AuthenticationState(new ClaimsPrincipal(identity)));

        var user = new User(Guid.NewGuid(), "Alice", "alice", "hash", "nt");
        _userContextFactory
            .Setup(f => f.CreateByUsernameAsync("alice"))
            .ReturnsAsync(new UserContext(user, [], [], []));

        // Benign data so the authorized happy-path can complete without a real DB.
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
        _userRepo.Setup(u => u.GetAllAsync()).ReturnsAsync(new List<User>());
        _groupRepo.Setup(g => g.GetAllAsync()).ReturnsAsync(new List<Group>());
        _roleRepo.Setup(r => r.GetAllAsync()).ReturnsAsync(new List<Role>());

        // Default: no sync folder covers the path (see AclEditorViewModelSyncFolderTests
        // for the enclosing-sync-folder behavior).
        _syncDefinitions
            .Setup(s => s.GetEnabledByShareAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<SyncDefinition>());

        _sut = new AclEditorViewModel(
            _aclRepo.Object, _metaRepo.Object, _userRepo.Object,
            _groupRepo.Object, _roleRepo.Object, _shareRepo.Object,
            _syncDefinitions.Object,
            _departmentRepo.Object, _deptPermissions.Object, _mgmtAuth.Object,
            _userContextFactory.Object, _authState.Object,
            NullLogger<AclEditorViewModel>.Instance);
    }

    /// <summary>Sets the scoped ManageShareAcls decision for the target share.</summary>
    private void Authorize(bool allowed) =>
        _mgmtAuth
            .Setup(m => m.CanManageShareAsync(
                It.IsAny<UserContext>(), _shareId, ManagementPermission.ManageShareAcls))
            .ReturnsAsync(allowed);

    // ═══════════════════ Load ═══════════════════

    [Fact]
    public async Task LoadAsync_WhenNotAuthorized_DoesNotLoadAndReportsAccessDenied()
    {
        Authorize(false);

        await _sut.LoadAsync("docs", _shareId, isDirectory: true);

        Assert.False(_sut.IsLoaded);
        Assert.Equal(Resources.Web_Error_AccessDenied, _sut.ErrorMessage);
        // No ACL data may be read for a share the user cannot manage.
        _metaRepo.Verify(
            m => m.GetOrCreateAsync(It.IsAny<string>(), It.IsAny<bool>(), It.IsAny<Guid>(), It.IsAny<Guid>()),
            Times.Never);
        _aclRepo.Verify(a => a.GetByFileMetadataIdAsync(It.IsAny<Guid>()), Times.Never);
    }

    [Fact]
    public async Task LoadAsync_WhenAuthorized_LoadsEntries()
    {
        Authorize(true);

        await _sut.LoadAsync("docs", _shareId, isDirectory: true);

        Assert.True(_sut.IsLoaded);
        Assert.Null(_sut.ErrorMessage);
        _metaRepo.Verify(
            m => m.GetOrCreateAsync("docs", true, It.IsAny<Guid>(), _shareId), Times.Once);
    }

    // ═══════════════════ Add ═══════════════════

    [Fact]
    public async Task AddEntryAsync_WhenNotAuthorized_ReturnsFalseAndDoesNotPersist()
    {
        // Loaded while authorized, then the right is revoked before the mutation.
        Authorize(true);
        await _sut.LoadAsync("docs", _shareId);
        Authorize(false);

        _sut.NewPrincipalId = Guid.NewGuid();
        _sut.NewPermissions = FilePermission.ReadAll;

        var ok = await _sut.AddEntryAsync();

        Assert.False(ok);
        Assert.Equal(Resources.Web_Error_AccessDenied, _sut.ErrorMessage);
        _aclRepo.Verify(a => a.AddAsync(It.IsAny<AccessEntry>()), Times.Never);
    }

    [Fact]
    public async Task AddEntryAsync_WhenAuthorized_Persists()
    {
        Authorize(true);
        await _sut.LoadAsync("docs", _shareId);

        _sut.NewPrincipalId = Guid.NewGuid();
        _sut.NewPermissions = FilePermission.ReadAll;

        var ok = await _sut.AddEntryAsync();

        Assert.True(ok);
        _aclRepo.Verify(a => a.AddAsync(It.IsAny<AccessEntry>()), Times.Once);
    }

    // ═══════════════════ Edit ═══════════════════

    [Fact]
    public async Task SaveEditEntryAsync_WhenNotAuthorized_ReturnsFalseAndDoesNotPersist()
    {
        var existing = new AccessEntry(
            Guid.NewGuid(), AclEntryType.Allow, FilePermission.ReadAll, AclInheritance.Everything);
        _aclRepo
            .Setup(a => a.GetByFileMetadataIdAsync(It.IsAny<Guid>()))
            .ReturnsAsync(new List<AccessEntry> { existing });

        Authorize(true);
        await _sut.LoadAsync("docs", _shareId);
        _sut.StartEditEntry(existing);
        _sut.NewPermissions = FilePermission.FullControl;
        Authorize(false);

        var ok = await _sut.SaveEditEntryAsync();

        Assert.False(ok);
        Assert.Equal(Resources.Web_Error_AccessDenied, _sut.ErrorMessage);
        _aclRepo.Verify(a => a.UpdateAsync(It.IsAny<AccessEntry>()), Times.Never);
    }

    // ═══════════════════ Delete ═══════════════════

    [Fact]
    public async Task DeleteEntryAsync_WhenNotAuthorized_ReturnsFalseAndDoesNotDelete()
    {
        Authorize(true);
        await _sut.LoadAsync("docs", _shareId);
        Authorize(false);

        var ok = await _sut.DeleteEntryAsync(Guid.NewGuid());

        Assert.False(ok);
        Assert.Equal(Resources.Web_Error_AccessDenied, _sut.ErrorMessage);
        _aclRepo.Verify(a => a.DeleteAsync(It.IsAny<Guid>()), Times.Never);
    }

    [Fact]
    public async Task DeleteEntryAsync_WhenAuthorized_Deletes()
    {
        Authorize(true);
        await _sut.LoadAsync("docs", _shareId);

        var ok = await _sut.DeleteEntryAsync(Guid.NewGuid());

        Assert.True(ok);
        _aclRepo.Verify(a => a.DeleteAsync(It.IsAny<Guid>()), Times.Once);
    }

    // ═══════════════════ Unauthenticated ═══════════════════

    [Fact]
    public async Task LoadAsync_WhenAnonymous_IsRefused()
    {
        // No authenticated identity → cannot resolve an actor → refuse regardless of mgmt setup.
        _authState
            .Setup(a => a.GetAuthenticationStateAsync())
            .ReturnsAsync(new AuthenticationState(new ClaimsPrincipal(new ClaimsIdentity())));
        Authorize(true);

        await _sut.LoadAsync("docs", _shareId);

        Assert.False(_sut.IsLoaded);
        _aclRepo.Verify(a => a.GetByFileMetadataIdAsync(It.IsAny<Guid>()), Times.Never);
    }
}

using System.Security.Claims;
using Kaimo_File_Server.Core.Domain;
using Kaimo_File_Server.Core.Domain.Identity;
using Kaimo_File_Server.Core.Repositories.ExternalStorage;
using Kaimo_File_Server.Core.Repositories.Files;
using Kaimo_File_Server.Core.Repositories.Identity;
using Kaimo_File_Server.Core.Repositories.Shares;
using Kaimo_File_Server.Core.Security.Acl;
using Kaimo_File_Server.Core.Security.Management;
using Kaimo_File_Server.Core.Services.Management;
using Kaimo_File_Server.Web.Services;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Kaimo_File_Server.Web.Tests.ViewModels.Acl;

/// <summary>
/// The ACL editor takes its principal list (users, groups, roles) from the shared
/// <see cref="PrincipalDirectoryCache"/>: a warm cache is shown instantly, and
/// <see cref="AclEditorViewModel.RevalidatePrincipalsAsync"/> refreshes it in the background,
/// reporting whether the picker has to re-render.
/// </summary>
public class AclEditorViewModelPrincipalCacheTests
{
    private readonly Mock<IAclRepository> _aclRepo = new();
    private readonly Mock<IFileMetadataRepository> _metaRepo = new();
    private readonly Mock<IShareRepository> _shareRepo = new();
    private readonly Mock<ISyncDefinitionRepository> _syncDefinitions = new();
    private readonly Mock<IDepartmentRepository> _departmentRepo = new();
    private readonly Mock<IDepartmentPermissionService> _deptPermissions = new();
    private readonly Mock<IManagementAuthService> _mgmtAuth = new();
    private readonly Mock<IUserContextFactory> _userContextFactory = new();
    private readonly Mock<AuthenticationStateProvider> _authState = new();

    private readonly Guid _shareId = Guid.NewGuid();
    private readonly PrincipalSummary _alice = new(Guid.NewGuid(), "Alice", "alice");
    private readonly PrincipalSummary _bob = new(Guid.NewGuid(), "Bob", "bob");
    private readonly PrincipalSummary _sales = new(Guid.NewGuid(), "Sales");

    private int _loads;
    private PrincipalDirectory _next;
    private Exception? _fail;
    private readonly PrincipalDirectoryCache _cache;
    private readonly AclEditorViewModel _sut;

    public AclEditorViewModelPrincipalCacheTests()
    {
        _next = new PrincipalDirectory([_alice], [_sales], []);
        _cache = new PrincipalDirectoryCache(() =>
        {
            _loads++;
            return _fail is { } ex ? Task.FromException<PrincipalDirectory>(ex) : Task.FromResult(_next);
        }, TimeSpan.Zero);

        var identity = new ClaimsIdentity(new[] { new Claim(ClaimTypes.Name, "admin") }, "test");
        _authState.Setup(a => a.GetAuthenticationStateAsync())
            .ReturnsAsync(new AuthenticationState(new ClaimsPrincipal(identity)));
        _userContextFactory.Setup(f => f.CreateByUsernameAsync("admin"))
            .ReturnsAsync(new UserContext(new User(Guid.NewGuid(), "Admin", "admin", "h", "n"), [], [], []));
        _metaRepo.Setup(m => m.GetOrCreateAsync(It.IsAny<string>(), It.IsAny<bool>(), It.IsAny<Guid>(), _shareId))
            .ReturnsAsync((string p, bool d, Guid _, Guid _) =>
                new FileMetadata { Id = Guid.NewGuid(), Path = p, Name = p, IsDirectory = d, Acl = [] });
        _aclRepo.Setup(a => a.GetByFileMetadataIdAsync(It.IsAny<Guid>())).ReturnsAsync(new List<AccessEntry>());
        _aclRepo.Setup(a => a.GetAclsForPathsAsync(It.IsAny<Guid>(), It.IsAny<List<string>>()))
            .ReturnsAsync(new List<(string, bool, List<AccessEntry>)>());
        _syncDefinitions.Setup(s => s.GetEnabledByShareAsync(_shareId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<SyncDefinition>());
        _shareRepo.Setup(s => s.GetByIdAsync(It.IsAny<Guid>())).ReturnsAsync((ShareDefinition?)null);

        _sut = new AclEditorViewModel(
            _aclRepo.Object, _metaRepo.Object, _cache, _shareRepo.Object,
            _syncDefinitions.Object, _departmentRepo.Object, _deptPermissions.Object,
            _mgmtAuth.Object, _userContextFactory.Object, _authState.Object,
            NullLogger<AclEditorViewModel>.Instance);
    }

    private void Authorized(bool allowed) =>
        _mgmtAuth.Setup(m => m.CanManageShareAsync(
                It.IsAny<UserContext>(), _shareId, ManagementPermission.ManageShareAcls))
            .ReturnsAsync(allowed);

    [Fact]
    public async Task Load_WarmCache_ServesCachedPrincipalsWithoutLoading()
    {
        Authorized(true);
        await _cache.GetAsync();

        await _sut.LoadAsync("docs", _shareId);

        Assert.Equal(1, _loads);
        Assert.Equal(new[] { _alice }, _sut.AllUsers);
        Assert.Equal(new[] { _sales }, _sut.AllGroups);
        Assert.Equal("Alice", _sut.GetPrincipalDisplayName(_alice.Id));
    }

    [Fact]
    public async Task Load_ColdCache_LoadsPrincipals()
    {
        Authorized(true);

        await _sut.LoadAsync("docs", _shareId);

        Assert.True(_sut.IsLoaded);
        Assert.Equal(1, _loads);
        Assert.Equal(new[] { _alice }, _sut.AllUsers);
    }

    [Fact]
    public async Task Revalidate_DataChanged_UpdatesListsAndReportsChange()
    {
        Authorized(true);
        await _sut.LoadAsync("docs", _shareId);
        _next = new PrincipalDirectory([_alice, _bob], [_sales], []);

        var changed = await _sut.RevalidatePrincipalsAsync();

        Assert.True(changed);
        Assert.Equal(new[] { _alice, _bob }, _sut.AllUsers);
        Assert.Equal("user", _sut.GetPrincipalType(_bob.Id));
    }

    [Fact]
    public async Task Revalidate_DataUnchanged_ReportsNoChange()
    {
        Authorized(true);
        await _sut.LoadAsync("docs", _shareId);
        _next = new PrincipalDirectory([_alice], [_sales], []);

        var changed = await _sut.RevalidatePrincipalsAsync();

        Assert.False(changed);
        Assert.Equal(2, _loads);
    }

    [Fact]
    public async Task Revalidate_OtherEditorRefreshedMeanwhile_PicksUpNewerSnapshot()
    {
        Authorized(true);
        await _sut.LoadAsync("docs", _shareId);
        _next = new PrincipalDirectory([_alice, _bob], [_sales], []);
        await _cache.RevalidateAsync(); // e.g. another circuit
        _next = new PrincipalDirectory([_alice, _bob], [_sales], []);

        Assert.True(await _sut.RevalidatePrincipalsAsync());
        Assert.Equal(2, _sut.AllUsers.Count);
    }

    [Fact]
    public async Task Revalidate_NotLoaded_DoesNothing()
    {
        Authorized(false);
        await _sut.LoadAsync("docs", _shareId);

        var changed = await _sut.RevalidatePrincipalsAsync();

        Assert.False(_sut.IsLoaded);
        Assert.False(changed);
        Assert.Equal(0, _loads);
        Assert.Empty(_sut.AllUsers);
    }

    [Fact]
    public async Task Revalidate_LoadFails_KeepsListsAndReportsNoChange()
    {
        Authorized(true);
        await _sut.LoadAsync("docs", _shareId);
        _fail = new InvalidOperationException("db down");

        var changed = await _sut.RevalidatePrincipalsAsync();

        Assert.False(changed);
        Assert.Equal(new[] { _alice }, _sut.AllUsers);
    }
}

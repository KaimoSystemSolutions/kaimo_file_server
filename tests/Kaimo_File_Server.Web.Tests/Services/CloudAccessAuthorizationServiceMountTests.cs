using Kaimo_File_Server.Core.Domain;
using Kaimo_File_Server.Core.Domain.Identity;
using Kaimo_File_Server.Core.Repositories.Shares;
using Kaimo_File_Server.Core.Security;
using Kaimo_File_Server.Core.Security.Acl;
using Kaimo_File_Server.Core.Security.Management;
using Kaimo_File_Server.Core.Services.Management;
using Kaimo_File_Server.Web.Services;
using Moq;
using Xunit;

namespace Kaimo_File_Server.Web.Tests.Services;

/// <summary>
/// A virtual share mounted as a folder grants the intersection of the host-share ACL at the
/// mount path and the virtual-share grant, for managers too.
/// </summary>
public sealed class CloudAccessAuthorizationServiceMountTests
{
    private readonly Mock<ICloudAccessRepository> _repository = new();
    private readonly Mock<IManagementAuthService> _management = new();
    private readonly Mock<IShareRepository> _shares = new();
    private readonly Mock<IAclService> _acl = new();
    private readonly UserContext _actor = new(new User(Guid.NewGuid(), "A", "alice", "h", "nt"), [], [], []);
    private readonly ShareDefinition _host = new("projekte", "/data/projekte", null, true, false, false);
    private readonly CloudAccessShare _share;

    public CloudAccessAuthorizationServiceMountTests()
    {
        _share = new CloudAccessShare
        {
            Name = "cloud", DepartmentId = Guid.NewGuid(), Provisioning = CloudAccessProvisioning.Folder,
            MountShareId = _host.Id, MountPath = "Team/Cloud"
        };
        _shares.Setup(s => s.GetByIdAsync(_host.Id)).ReturnsAsync(_host);
        Manager(false);
        Grant(CloudAccessPermission.Write);
    }

    private CloudAccessAuthorizationService Sut(bool withHostDeps = true) => withHostDeps
        ? new(_repository.Object, _management.Object, _shares.Object, _acl.Object)
        : new(_repository.Object, _management.Object);

    private void Manager(bool isManager) => _management
        .Setup(m => m.GetAuthorizedDepartmentIdsAsync(_actor, ManagementPermission.ManageCloudAccess))
        .ReturnsAsync(isManager ? AuthorizedScopeResult.Unrestricted() : AuthorizedScopeResult.None());

    private void Grant(CloudAccessPermission? permission) => _repository
        .Setup(r => r.GetEffectivePermissionAsync(_share.Id, It.IsAny<IEnumerable<Guid>>()))
        .ReturnsAsync(permission);

    private void HostAcl(params FilePermission[] allowed)
    {
        _acl.Setup(a => a.HasAccessAsync(_actor, _host.Id, "Team/Cloud", true, It.IsAny<FilePermission>()))
            .ReturnsAsync((UserContext _, Guid _, string _, bool _, FilePermission p) => allowed.Contains(p));
    }

    private Task<CloudAccessPermission?> Effective(bool withHostDeps = true)
        => Sut(withHostDeps).GetEffectivePermissionAsync(_actor, _share);

    [Fact]
    public async Task FullHostAccess_AndWriteGrant_IsWrite()
    {
        HostAcl(FilePermission.ListReadData, FilePermission.CreateWriteData, FilePermission.Delete);
        Assert.Equal(CloudAccessPermission.Write, await Effective());
    }

    [Fact]
    public async Task ReadOnlyHost_ClampsWriteGrantToRead()
    {
        HostAcl(FilePermission.ListReadData);
        Assert.Equal(CloudAccessPermission.Read, await Effective());
    }

    [Fact]
    public async Task HostWriteWithoutDelete_IsRead()
    {
        HostAcl(FilePermission.ListReadData, FilePermission.CreateWriteData);
        Assert.Equal(CloudAccessPermission.Read, await Effective());
    }

    [Fact]
    public async Task NoHostRead_IsNoAccess()
    {
        HostAcl(FilePermission.CreateWriteData, FilePermission.Delete);
        Assert.Null(await Effective());
    }

    [Fact]
    public async Task ReadGrant_WithFullHost_StaysRead()
    {
        Grant(CloudAccessPermission.Read);
        HostAcl(FilePermission.ListReadData, FilePermission.CreateWriteData, FilePermission.Delete);
        Assert.Equal(CloudAccessPermission.Read, await Effective());
    }

    [Fact]
    public async Task NoGrant_WithFullHost_IsNoAccess()
    {
        Grant(null);
        HostAcl(FilePermission.ListReadData, FilePermission.CreateWriteData, FilePermission.Delete);
        Assert.Null(await Effective());
    }

    [Fact]
    public async Task Manager_WithoutHostAcl_IsNoAccess()
    {
        Manager(true);
        HostAcl();
        Assert.Null(await Effective());
    }

    [Fact]
    public async Task OrphanedFolderMode_IsNoAccess()
    {
        _share.MountShareId = null;
        _share.MountPath = null;
        HostAcl(FilePermission.ListReadData);
        Assert.Null(await Effective());
    }

    [Fact]
    public async Task DisabledHost_IsNoAccess()
    {
        _host.IsEnabled = false;
        HostAcl(FilePermission.ListReadData);
        Assert.Null(await Effective());
    }

    [Fact]
    public async Task HomesHost_IsNoAccess()
    {
        _host.IsUserHomes = true;
        HostAcl(FilePermission.ListReadData);
        Assert.Null(await Effective());
    }

    [Fact]
    public async Task FolderMode_WithoutHostDependencies_FailsClosed()
        => Assert.Null(await Effective(withHostDeps: false));

    [Fact]
    public async Task VisibleShares_ExcludeFolderModeUnlessRequested()
    {
        HostAcl(FilePermission.ListReadData);
        _repository.Setup(r => r.GetSharesAsync()).ReturnsAsync([_share]);

        Assert.Empty(await Sut().GetVisibleSharesAsync(_actor));
        Assert.Single(await Sut().GetVisibleSharesAsync(_actor, includeFolderMode: true));
    }

    [Fact]
    public async Task VirtualShareMode_IsUnchanged()
    {
        _share.Provisioning = CloudAccessProvisioning.VirtualShare;
        Assert.Equal(CloudAccessPermission.Write, await Effective(withHostDeps: false));
    }
}

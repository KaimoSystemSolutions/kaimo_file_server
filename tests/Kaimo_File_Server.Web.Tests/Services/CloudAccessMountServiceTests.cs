using Kaimo_File_Server.Core.Domain;
using Kaimo_File_Server.Core.Domain.Identity;
using Kaimo_File_Server.Core.Repositories.Shares;
using Kaimo_File_Server.Core.Security.Management;
using Kaimo_File_Server.Core.Services.Management;
using Kaimo_File_Server.Web.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Kaimo_File_Server.Web.Tests.Services;

public sealed class CloudAccessMountServiceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "kaimo-mount-" + Guid.NewGuid().ToString("N"));
    private readonly Mock<ICloudAccessRepository> _repository = new();
    private readonly Mock<IShareRepository> _shares = new();
    private readonly Mock<IManagementAuthService> _management = new();
    private readonly UserContext _actor = new(new User(Guid.NewGuid(), "A", "admin", "h", "nt"), [], [], []);
    private readonly ShareDefinition _host;
    private readonly CloudAccessShare _share = new() { Name = "remote", DepartmentId = Guid.NewGuid() };
    private readonly List<CloudAccessShare> _all = [];

    public CloudAccessMountServiceTests()
    {
        Directory.CreateDirectory(Path.Combine(_root, "Team", "Existing"));
        _host = new ShareDefinition("projekte", _root, null, true, false, false);
        _all.Add(_share);
        _shares.Setup(s => s.GetByIdAsync(_host.Id)).ReturnsAsync(_host);
        _repository.Setup(r => r.GetSharesAsync(It.IsAny<CancellationToken>())).ReturnsAsync(_all);
        Rights(cloud: true, host: true);
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private void Rights(bool cloud, bool host)
    {
        _management.Setup(m => m.GetAuthorizedDepartmentIdsAsync(_actor, ManagementPermission.ManageCloudAccess))
            .ReturnsAsync(cloud ? AuthorizedScopeResult.Unrestricted() : AuthorizedScopeResult.None());
        _management.Setup(m => m.CanManageShareAsync(_actor, _host.Id, ManagementPermission.EditShareSettings))
            .ReturnsAsync(host);
    }

    private CloudAccessMountService Sut() => new(
        _repository.Object, _shares.Object,
        new CloudAccessAuthorizationService(_repository.Object, _management.Object),
        _management.Object, NullLogger<CloudAccessMountService>.Instance);

    private Task<string?> Mount(string parent = "Team", string name = "Cloud")
        => Sut().MountAsync(_actor, _share, _host.Id, parent, name);

    private void VerifyNotSaved() => _repository.Verify(r => r.SetMountAsync(
        It.IsAny<Guid>(), It.IsAny<CloudAccessProvisioning>(), It.IsAny<Guid?>(), It.IsAny<string?>(),
        It.IsAny<CancellationToken>()), Times.Never);

    [Fact]
    public async Task ValidMount_IsSaved()
    {
        Assert.Null(await Mount());
        _repository.Verify(r => r.SetMountAsync(_share.Id, CloudAccessProvisioning.Folder, _host.Id, "Team/Cloud",
            It.IsAny<CancellationToken>()), Times.Once);
        Assert.True(_share.IsMounted);
    }

    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public async Task MissingAdminRightOnEitherSide_IsRejected(bool cloud, bool host)
    {
        Rights(cloud, host);
        Assert.Equal("Web_CloudAccess_Mount_Error_NotAllowed", await Mount());
        VerifyNotSaved();
    }

    [Fact]
    public async Task HomesShare_IsRejected()
    {
        _host.IsUserHomes = true;
        Assert.Equal("Web_CloudAccess_Mount_Error_HomesShare", await Mount());
    }

    [Theory]
    [InlineData("", "", "Web_CloudAccess_Mount_Error_RootNotAllowed")]
    [InlineData("Team", "a:b", "Web_CloudAccess_Mount_Error_InvalidPath")]
    [InlineData("../x", "Cloud", "Web_CloudAccess_Mount_Error_InvalidPath")]
    [InlineData("", ".RECYCLE_BIN", "Web_CloudAccess_Mount_Error_ReservedName")]
    [InlineData("Team", ".kaimo-versions", "Web_CloudAccess_Mount_Error_ReservedName")]
    [InlineData("Missing", "Cloud", "Web_CloudAccess_Mount_Error_ParentMissing")]
    [InlineData("Team", "existing", "Web_CloudAccess_Mount_Error_NameInUse")]
    public async Task InvalidTargets_AreRejected(string parent, string name, string error)
    {
        Assert.Equal(error, await Mount(parent, name));
        VerifyNotSaved();
    }

    [Theory]
    [InlineData("Team/Cloud")]
    [InlineData("Team")]
    [InlineData("Team/Cloud/Deep")]
    public async Task NestedMounts_AreRejected(string otherPath)
    {
        _all.Add(new CloudAccessShare
        {
            Name = "other", Provisioning = CloudAccessProvisioning.Folder, MountShareId = _host.Id, MountPath = otherPath
        });
        Assert.Equal("Web_CloudAccess_Mount_Error_Nested", await Mount());
    }

    [Fact]
    public async Task UniqueIndexViolation_ReportsNameInUse()
    {
        _repository.Setup(r => r.SetMountAsync(It.IsAny<Guid>(), It.IsAny<CloudAccessProvisioning>(),
                It.IsAny<Guid?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new DbUpdateException());
        Assert.Equal("Web_CloudAccess_Mount_Error_NameInUse", await Mount());
    }

    [Fact]
    public async Task FolderCreatedDuringSave_RollsBack()
    {
        _repository.Setup(r => r.SetMountAsync(_share.Id, CloudAccessProvisioning.Folder, _host.Id, "Team/Cloud",
                It.IsAny<CancellationToken>()))
            .Callback(() => Directory.CreateDirectory(Path.Combine(_root, "Team", "Cloud")))
            .Returns(Task.CompletedTask);

        Assert.Equal("Web_CloudAccess_Mount_Error_NameInUse", await Mount());
        _repository.Verify(r => r.SetMountAsync(_share.Id, CloudAccessProvisioning.VirtualShare, null, null,
            It.IsAny<CancellationToken>()), Times.Once);
        Assert.False(_share.IsMounted);
    }

    [Fact]
    public async Task Unmount_SwitchesBackToVirtualShare()
    {
        _share.Provisioning = CloudAccessProvisioning.Folder;
        Assert.Null(await Sut().UnmountAsync(_actor, _share));
        _repository.Verify(r => r.SetMountAsync(_share.Id, CloudAccessProvisioning.VirtualShare, null, null,
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public void HostFolderPicker_HidesReservedFolders()
    {
        Directory.CreateDirectory(Path.Combine(_root, ".RECYCLE_BIN"));
        Assert.Equal([("Team", "Team")], Sut().ListHostFolders(_host, ""));
    }
}

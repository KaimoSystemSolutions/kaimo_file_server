using Kaimo_File_Server.Core.Domain;
using Kaimo_File_Server.Core.Domain.ClientSync;
using Kaimo_File_Server.Core.Domain.Identity;
using Kaimo_File_Server.Core.Repositories.ClientSync;
using Kaimo_File_Server.Core.Repositories.Identity;
using Kaimo_File_Server.Core.Repositories.Shares;
using Kaimo_File_Server.Core.Security.Management;
using Kaimo_File_Server.Core.Services.Management;
using Kaimo_File_Server.Web.Components.ViewModels;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using System.Security.Claims;
using Xunit;

namespace Kaimo_File_Server.Web.Tests.ViewModels.Settings;

/// <summary>
/// The synced folders of a client device show a home folder (named by its owner's GUID) as the
/// owner's username, like the file browser does; folders of other shares keep their real path.
/// </summary>
public class ClientDeviceAdminFolderLabelTests
{
    private readonly User _admin = new(Guid.NewGuid(), "Admin", "admin", "hash", "nt");
    private readonly User _bob = new(Guid.NewGuid(), "Bob", "bob", "hash", "nt");
    private readonly ShareDefinition _homes = new("users", "/data/users", isEnabled: true) { IsUserHomes = true };
    private readonly ShareDefinition _projects = new("projects", "/data/projects", isEnabled: true);
    private readonly SyncDevice _device;
    private readonly List<DeviceSyncProfile> _profiles = [];

    public ClientDeviceAdminFolderLabelTests()
    {
        _device = new SyncDevice { UserId = _bob.Id, DisplayName = "Bob's laptop", Platform = "linux" };
    }

    private DeviceSyncProfile AddProfile(ShareDefinition share, string relativePath)
    {
        var profile = new DeviceSyncProfile
        {
            DeviceId = _device.Id, UserId = _bob.Id, ShareId = share.Id, RelativePath = relativePath,
        };
        _profiles.Add(profile);
        return profile;
    }

    private async Task<ClientDeviceAdminViewModel> LoadedAsync()
    {
        var identity = new ClaimsIdentity([new Claim(ClaimTypes.Name, _admin.Username)], "test");
        var authState = new Mock<AuthenticationStateProvider>();
        authState.Setup(a => a.GetAuthenticationStateAsync())
            .ReturnsAsync(new AuthenticationState(new ClaimsPrincipal(identity)));

        var contexts = new Mock<IUserContextFactory>();
        contexts.Setup(f => f.CreateByUsernameAsync(_admin.Username)).ReturnsAsync(new UserContext(_admin, [], [], []));
        contexts.Setup(f => f.CreateByUserIdAsync(_bob.Id)).ReturnsAsync(new UserContext(_bob, [], [], []));

        var auth = new Mock<IManagementAuthService>();
        auth.Setup(a => a.HasGlobalPermissionAsync(It.IsAny<UserContext>(), ManagementPermission.ManageClientDevices))
            .ReturnsAsync(true);

        var devices = new Mock<ISyncDeviceRepository>();
        devices.Setup(d => d.GetAllAsync()).ReturnsAsync([_device]);
        var profiles = new Mock<IDeviceSyncProfileRepository>();
        profiles.Setup(p => p.GetByDeviceAsync(_device.Id)).ReturnsAsync(_profiles);
        var shares = new Mock<IShareRepository>();
        shares.Setup(s => s.GetByIdAsync(_homes.Id)).ReturnsAsync(_homes);
        shares.Setup(s => s.GetByIdAsync(_projects.Id)).ReturnsAsync(_projects);

        var vm = new ClientDeviceAdminViewModel(authState.Object, contexts.Object, auth.Object, devices.Object,
            profiles.Object, Mock.Of<IRefreshTokenRepository>(), shares.Object, TimeProvider.System,
            NullLogger<ClientDeviceAdminViewModel>.Instance);
        await vm.LoadAsync();
        Assert.False(vm.LoadFailed);
        return vm;
    }

    [Fact]
    public async Task A_home_folder_is_shown_as_its_owners_username()
    {
        var home = AddProfile(_homes, _bob.Id.ToString("D"));
        var below = AddProfile(_homes, $"{_bob.Id:D}/Documents/Work");
        var vm = await LoadedAsync();

        Assert.Equal("bob", vm.FolderPath(home));
        Assert.Equal("bob/Documents/Work", vm.FolderPath(below));
    }

    [Fact]
    public async Task A_home_whose_owner_no_longer_exists_keeps_its_guid()
    {
        var gone = Guid.NewGuid().ToString("D");
        var profile = AddProfile(_homes, gone + "/Docs");
        var vm = await LoadedAsync();

        Assert.Equal(gone + "/Docs", vm.FolderPath(profile));
    }

    [Fact]
    public async Task Other_shares_keep_their_real_path_even_when_a_segment_looks_like_a_user_id()
    {
        var profile = AddProfile(_projects, $"{_bob.Id:D}/Docs");
        var vm = await LoadedAsync();

        Assert.Equal($"{_bob.Id:D}/Docs", vm.FolderPath(profile));
    }

    [Fact]
    public async Task Only_a_folder_named_like_a_home_is_relabelled()
    {
        // Homes are named by the "D" form of the owner's id; another spelling is no home.
        var profile = AddProfile(_homes, $"{_bob.Id:N}/Docs");
        var vm = await LoadedAsync();

        Assert.Equal($"{_bob.Id:N}/Docs", vm.FolderPath(profile));
    }

    [Fact]
    public async Task The_share_root_is_shown_as_a_slash()
    {
        var profile = AddProfile(_projects, "");
        var vm = await LoadedAsync();

        Assert.Equal("/", vm.FolderPath(profile));
    }
}

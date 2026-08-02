using System.Security.Claims;
using Kaimo_File_Server.Core.Domain.Identity;
using Kaimo_File_Server.Core.Repositories;
using Kaimo_File_Server.Core.Security;
using Kaimo_File_Server.Core.Services;
using Kaimo_File_Server.Core.Services.DataServices;
using Kaimo_File_Server.Infrastructure.Configuration;
using Kaimo_File_Server.Search;
using Kaimo_File_Server.Web.Components.ViewModels;
using Kaimo_File_Server.Web.Services.Https;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Kaimo_File_Server.Tests;

/// <summary>
/// Covers the SMB protocol/security settings surface: the <see cref="SmbProtocolSettings"/>
/// config model (defaults + range normalization) and the <see cref="SettingsViewModel"/>
/// load/save behaviour for those settings, including permission gating and range repair.
/// </summary>
public class SettingsViewModelSmbTests
{
    // ─────────────────────────── SmbProtocolSettings model ───────────────────────────

    [Fact]
    public void Default_ReproducesLibrarySecureDefaults()
    {
        var s = SmbProtocolSettings.Default();

        Assert.Equal(SmbProtocolVersion.Smb202, s.MinVersion);
        Assert.Equal(SmbProtocolVersion.Smb311, s.MaxVersion);
        Assert.True(s.RequireSigning);
        Assert.False(s.RequireEncryption);
        // Discoverability + auditing are on by default (usability / security posture).
        Assert.True(s.EnableWsDiscovery);
        Assert.True(s.EnableAuditLog);
    }

    [Fact]
    public void Normalize_InvertedRange_RaisesMaxToMin()
    {
        var s = new SmbProtocolSettings
        {
            MinVersion = SmbProtocolVersion.Smb311,
            MaxVersion = SmbProtocolVersion.Smb202,
        };

        s.Normalize();

        Assert.Equal(SmbProtocolVersion.Smb311, s.MinVersion);
        Assert.Equal(SmbProtocolVersion.Smb311, s.MaxVersion);
    }

    [Fact]
    public void Normalize_ValidRange_LeavesUntouched()
    {
        var s = new SmbProtocolSettings
        {
            MinVersion = SmbProtocolVersion.Smb210,
            MaxVersion = SmbProtocolVersion.Smb302,
        };

        s.Normalize();

        Assert.Equal(SmbProtocolVersion.Smb210, s.MinVersion);
        Assert.Equal(SmbProtocolVersion.Smb302, s.MaxVersion);
    }

    [Fact]
    public void Normalize_EqualMinAndMax_IsAllowed()
    {
        var s = new SmbProtocolSettings
        {
            MinVersion = SmbProtocolVersion.Smb300,
            MaxVersion = SmbProtocolVersion.Smb300,
        };

        s.Normalize();

        Assert.Equal(SmbProtocolVersion.Smb300, s.MinVersion);
        Assert.Equal(SmbProtocolVersion.Smb300, s.MaxVersion);
    }

    // ─────────────────────────── SettingsViewModel: load ───────────────────────────

    [Fact]
    public async Task LoadAsync_WithDataServicePermission_LoadsProtocolSettings()
    {
        var stored = new SmbProtocolSettings
        {
            MinVersion = SmbProtocolVersion.Smb210,
            MaxVersion = SmbProtocolVersion.Smb311,
            RequireSigning = false,
            RequireEncryption = true,
        };
        var h = new Harness(canManageDataServices: true, storedProtocol: stored);

        await h.Vm.LoadAsync();

        Assert.True(h.Vm.CanManageDataServices);
        Assert.Equal(SmbProtocolVersion.Smb210, h.Vm.SmbProtocol.MinVersion);
        Assert.Equal(SmbProtocolVersion.Smb311, h.Vm.SmbProtocol.MaxVersion);
        Assert.False(h.Vm.SmbProtocol.RequireSigning);
        Assert.True(h.Vm.SmbProtocol.RequireEncryption);
    }

    [Fact]
    public async Task LoadAsync_InvertedStoredRange_IsNormalized()
    {
        var stored = new SmbProtocolSettings
        {
            MinVersion = SmbProtocolVersion.Smb311,
            MaxVersion = SmbProtocolVersion.Smb202,
        };
        var h = new Harness(canManageDataServices: true, storedProtocol: stored);

        await h.Vm.LoadAsync();

        // Max was raised to Min so at least one dialect is negotiable.
        Assert.Equal(SmbProtocolVersion.Smb311, h.Vm.SmbProtocol.MinVersion);
        Assert.Equal(SmbProtocolVersion.Smb311, h.Vm.SmbProtocol.MaxVersion);
    }

    [Fact]
    public async Task LoadAsync_WithoutDataServicePermission_DoesNotReadProtocolSettings()
    {
        var h = new Harness(canManageDataServices: false);

        await h.Vm.LoadAsync();

        Assert.False(h.Vm.CanManageDataServices);
        h.Config.Verify(
            c => c.GetAsync(SmbProtocolSettings.ConfigKey, It.IsAny<SmbProtocolSettings>()),
            Times.Never);
    }

    [Fact]
    public async Task LoadAsync_WithSettingsPermission_LoadsAllStoragePools()
    {
        var pools = new List<StorageUsageInfo>
        {
            new("/data/storage/pool01", "/dev/sda", 1_000, 400, 600, true),
            new("/data/storage/pool02", "/dev/sdb", 2_000, 500, 1_500, true),
        };
        var systemInfo = new Mock<ISystemInfoService>();
        systemInfo.SetupGet(s => s.HostName).Returns("kaimo");
        systemInfo.Setup(s => s.GetNetworkAddresses()).Returns([]);
        systemInfo.Setup(s => s.GetStorageUsage()).Returns(pools);
        systemInfo.Setup(s => s.GetMemoryUsage()).Returns(
            new MemoryUsageInfo(100, 100, 50, 1_000));

        var h = new Harness(
            canManageDataServices: false,
            canManageSettings: true,
            systemInfo: systemInfo.Object);

        await h.Vm.LoadAsync();

        Assert.Equal(pools, h.Vm.StorageUsages);
        systemInfo.Verify(s => s.GetStorageUsage(), Times.Once);
    }

    [Fact]
    public async Task LoadAsync_WithSettingsPermission_DefersSearchProbeUntilSearchTabLoads()
    {
        var h = new Harness(canManageDataServices: false, canManageSettings: true);

        await h.Vm.LoadAsync();

        h.SearchAdmin.Verify(
            s => s.GetStateAsync(It.IsAny<CancellationToken>()), Times.Never);

        await h.Vm.LoadSearchStateAsync();

        h.SearchAdmin.Verify(
            s => s.GetStateAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task RefreshMemoryInfo_DoesNotRefreshNetworkOrStorage()
    {
        var systemInfo = new Mock<ISystemInfoService>();
        systemInfo.SetupGet(s => s.HostName).Returns("kaimo");
        systemInfo.Setup(s => s.GetNetworkAddresses()).Returns([]);
        systemInfo.Setup(s => s.GetStorageUsage()).Returns([]);
        systemInfo.Setup(s => s.GetMemoryUsage()).Returns(
            new MemoryUsageInfo(100, 100, 50, 1_000));
        var h = new Harness(
            canManageDataServices: false,
            canManageSettings: true,
            systemInfo: systemInfo.Object);
        await h.Vm.LoadAsync();
        systemInfo.Invocations.Clear();

        h.Vm.RefreshMemoryInfo();

        systemInfo.Verify(s => s.GetMemoryUsage(), Times.Once);
        systemInfo.Verify(s => s.GetNetworkAddresses(), Times.Never);
        systemInfo.Verify(s => s.GetStorageUsage(), Times.Never);
    }

    // ─────────────────────────── SettingsViewModel: save ───────────────────────────

    [Fact]
    public async Task SaveSmbProtocolAsync_Persists_UnderProtocolKey()
    {
        var h = new Harness(canManageDataServices: true);
        await h.Vm.LoadAsync();

        h.Vm.SmbProtocol.MinVersion = SmbProtocolVersion.Smb300;
        h.Vm.SmbProtocol.MaxVersion = SmbProtocolVersion.Smb311;
        h.Vm.SmbProtocol.RequireEncryption = true;

        var ok = await h.Vm.SaveSmbProtocolAsync();

        Assert.True(ok);
        Assert.NotNull(h.SavedProtocol);
        Assert.Equal(SmbProtocolVersion.Smb300, h.SavedProtocol!.MinVersion);
        Assert.Equal(SmbProtocolVersion.Smb311, h.SavedProtocol.MaxVersion);
        Assert.True(h.SavedProtocol.RequireEncryption);
        Assert.NotNull(h.Vm.SuccessMessage);
        Assert.Null(h.Vm.ErrorMessage);
    }

    [Fact]
    public async Task SaveSmbProtocolAsync_Persists_DiscoveryAndAuditFlags()
    {
        var h = new Harness(canManageDataServices: true);
        await h.Vm.LoadAsync();

        h.Vm.SmbProtocol.EnableWsDiscovery = false;
        h.Vm.SmbProtocol.EnableAuditLog = false;

        var ok = await h.Vm.SaveSmbProtocolAsync();

        Assert.True(ok);
        Assert.NotNull(h.SavedProtocol);
        Assert.False(h.SavedProtocol!.EnableWsDiscovery);
        Assert.False(h.SavedProtocol.EnableAuditLog);
    }

    [Fact]
    public async Task SaveSmbProtocolAsync_InvertedRange_IsNormalizedBeforeWrite()
    {
        var h = new Harness(canManageDataServices: true);
        await h.Vm.LoadAsync();

        h.Vm.SmbProtocol.MinVersion = SmbProtocolVersion.Smb311;
        h.Vm.SmbProtocol.MaxVersion = SmbProtocolVersion.Smb202;

        var ok = await h.Vm.SaveSmbProtocolAsync();

        Assert.True(ok);
        Assert.NotNull(h.SavedProtocol);
        Assert.Equal(SmbProtocolVersion.Smb311, h.SavedProtocol!.MinVersion);
        Assert.Equal(SmbProtocolVersion.Smb311, h.SavedProtocol.MaxVersion);
    }

    [Fact]
    public async Task SaveSmbProtocolAsync_WithoutPermission_FailsAndDoesNotWrite()
    {
        var h = new Harness(canManageDataServices: false);
        await h.Vm.LoadAsync();

        var ok = await h.Vm.SaveSmbProtocolAsync();

        Assert.False(ok);
        Assert.NotNull(h.Vm.ErrorMessage);
        h.Config.Verify(
            c => c.SetAsync(SmbProtocolSettings.ConfigKey, It.IsAny<SmbProtocolSettings>()),
            Times.Never);
    }

    [Fact]
    public async Task SaveSmbProtocolAsync_WhenStoreThrows_ReportsError()
    {
        var h = new Harness(canManageDataServices: true);
        await h.Vm.LoadAsync();
        h.Config
            .Setup(c => c.SetAsync(SmbProtocolSettings.ConfigKey, It.IsAny<SmbProtocolSettings>()))
            .ThrowsAsync(new InvalidOperationException("db down"));

        var ok = await h.Vm.SaveSmbProtocolAsync();

        Assert.False(ok);
        Assert.NotNull(h.Vm.ErrorMessage);
        Assert.Null(h.Vm.SuccessMessage);
    }

    // ─────────────────────────── Test harness ───────────────────────────

    private sealed class Harness
    {
        public Mock<IConfigRepository> Config { get; } = new();
        public Mock<ISearchAdminService> SearchAdmin { get; } = new();
        public SettingsViewModel Vm { get; }

        /// <summary>The <see cref="SmbProtocolSettings"/> captured on the last SetAsync write.</summary>
        public SmbProtocolSettings? SavedProtocol { get; private set; }

        public Harness(
            bool canManageDataServices,
            SmbProtocolSettings? storedProtocol = null,
            bool canManageSettings = false,
            ISystemInfoService? systemInfo = null)
        {
            storedProtocol ??= SmbProtocolSettings.Default();

            Config.Setup(c => c.GetBoolAsync(It.IsAny<string>(), It.IsAny<bool>()))
                .ReturnsAsync(true);
            Config.Setup(c => c.GetAsync(SmbProtocolSettings.ConfigKey, It.IsAny<SmbProtocolSettings>()))
                .ReturnsAsync(storedProtocol);
            Config.Setup(c => c.GetFreshAsync(It.IsAny<string>(), It.IsAny<string>()))
                .ReturnsAsync("Running");
            Config.Setup(c => c.SetAsync(SmbProtocolSettings.ConfigKey, It.IsAny<SmbProtocolSettings>()))
                .Callback<string, SmbProtocolSettings>((_, v) => SavedProtocol = v)
                .Returns(Task.CompletedTask);

            var actor = MakeActor();
            var userFactory = new Mock<IUserContextFactory>();
            userFactory.Setup(f => f.CreateByUsernameAsync(It.IsAny<string>()))
                .ReturnsAsync(actor);

            var mgmt = new Mock<IManagementAuthService>();
            var globalPermissions = ManagementPermission.None;
            if (canManageDataServices)
                globalPermissions |= ManagementPermission.ManageDataServices;
            if (canManageSettings)
                globalPermissions |= ManagementPermission.ManageSystemSettings;
            mgmt.Setup(m => m.GetEffectivePermissionsAtAsync(
                    It.IsAny<UserContext>(), ScopeType.Global, Guid.Empty))
                .ReturnsAsync(globalPermissions);

            SearchAdmin.Setup(s => s.GetStateAsync(It.IsAny<CancellationToken>()))
                .ReturnsAsync(new SearchEngineState(true, true, true));
            SearchAdmin.Setup(s => s.GetReindexProgress()).Returns(ReindexProgress.Idle);

            var loggingStore = new Mock<Kaimo_File_Server.Core.Logging.ILoggingConfigStore>();
            loggingStore.Setup(s => s.GetLevelAsync()).ReturnsAsync("Warning");

            Vm = new SettingsViewModel(
                Config.Object,
                mgmt.Object,
                userFactory.Object,
                new StubAuthProvider("admin"),
                systemInfo ?? Mock.Of<ISystemInfoService>(),
                SearchAdmin.Object,
                Mock.Of<IHttpsCertificateProvider>(),
                loggingStore.Object,
                new Kaimo_File_Server.Infrastructure.Logging.LoggingLevelConfigurationSource(),
                NullLogger<SettingsViewModel>.Instance);
        }

        private static UserContext MakeActor()
        {
            var user = new User(Guid.NewGuid(), "Admin", "admin", "pw-hash", "nt-hash");
            return new UserContext(user, new HashSet<Group>(), new HashSet<Role>(), new HashSet<string>());
        }
    }

    private sealed class StubAuthProvider : AuthenticationStateProvider
    {
        private readonly AuthenticationState _state;

        public StubAuthProvider(string username)
        {
            var identity = new ClaimsIdentity(
                new[] { new Claim(ClaimTypes.Name, username) }, authenticationType: "test");
            _state = new AuthenticationState(new ClaimsPrincipal(identity));
        }

        public override Task<AuthenticationState> GetAuthenticationStateAsync()
            => Task.FromResult(_state);
    }
}

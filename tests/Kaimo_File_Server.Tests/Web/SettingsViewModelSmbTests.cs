using System.Security.Claims;
using Kaimo_File_Server.Core.Domain.Identity;
using Kaimo_File_Server.Core.Repositories;
using Kaimo_File_Server.Core.Security;
using Kaimo_File_Server.Core.Services;
using Kaimo_File_Server.Core.Services.DataServices;
using Kaimo_File_Server.Infrastructure.Configuration;
using Kaimo_File_Server.Infrastructure.Services;
using Kaimo_File_Server.Search;
using Kaimo_File_Server.Web.Components.ViewModels;
using Kaimo_File_Server.Web.Services;
using Kaimo_File_Server.Web.Services.Https;
using Bunit;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Kaimo_File_Server.Tests;

/// <summary>
/// Covers the SMB protocol/security settings surface: the <see cref="SmbProtocolSettings"/>
/// config model (defaults + range normalization) and the <see cref="SettingsViewModel"/>
/// load/save behaviour for those settings, including permission gating and range repair.
/// </summary>
public partial class SettingsViewModelSmbTests
{
    [Fact]
    public async Task SaveCloudAccessSettingsAsync_ClampsAndPersistsGlobalTtl()
    {
        var h = new Harness(canManageDataServices: false, canManageSettings: true);
        await h.Vm.LoadAsync();
        h.Vm.CloudAccessSettings.DirectoryCacheSeconds = 999;

        var ok = await h.Vm.SaveCloudAccessSettingsAsync();

        Assert.True(ok);
        Assert.Equal(CloudAccessRuntimeSettings.MaxDirectoryCacheSeconds,
            h.Vm.CloudAccessSettings.DirectoryCacheSeconds);
        h.CloudAccessSettings.Verify(s => s.SetAsync(It.Is<CloudAccessRuntimeSettings>(value =>
            value.DirectoryCacheSeconds == CloudAccessRuntimeSettings.MaxDirectoryCacheSeconds)), Times.Once);
    }

    [Theory]
    [InlineData(5L << 20, 5L << 20)]
    [InlineData(null, 0L)]
    public async Task SaveRecycleGlobalMaxSizeAsync_PersistsBytesOrZeroForUnlimited(long? input, long stored)
    {
        var h = new Harness(canManageDataServices: false, canManageSettings: true);
        await h.Vm.LoadAsync();

        var ok = await h.Vm.SaveRecycleGlobalMaxSizeAsync(input);

        Assert.True(ok);
        h.Config.Verify(c => c.SetAsync(RecycleBinJanitor.GlobalMaxSizeConfigKey, stored), Times.Once);
        Assert.Equal(stored, h.Vm.RecycleGlobalMaxSizeBytes);
    }

    [Fact]
    public async Task SaveRecycleGlobalMaxSizeAsync_BelowMinimum_IsRejected()
    {
        var h = new Harness(canManageDataServices: false, canManageSettings: true);
        await h.Vm.LoadAsync();

        var ok = await h.Vm.SaveRecycleGlobalMaxSizeAsync(1000);

        Assert.False(ok);
        h.Config.Verify(c => c.SetAsync(RecycleBinJanitor.GlobalMaxSizeConfigKey, It.IsAny<long>()), Times.Never);
    }

    [Fact]
    public async Task SaveRecycleGlobalMaxSizeAsync_WithoutPermission_IsDenied()
    {
        var h = new Harness(canManageDataServices: true);
        await h.Vm.LoadAsync();

        var ok = await h.Vm.SaveRecycleGlobalMaxSizeAsync(5L << 20);

        Assert.False(ok);
        h.Config.Verify(c => c.SetAsync(RecycleBinJanitor.GlobalMaxSizeConfigKey, It.IsAny<long>()), Times.Never);
    }

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

    // ─────────────────────────── SettingsViewModel: storage origin ───────────────────────────

    private static Mock<ISystemInfoService> SystemInfoWith(params StorageUsageInfo[] pools)
    {
        var systemInfo = new Mock<ISystemInfoService>();
        systemInfo.SetupGet(s => s.HostName).Returns("kaimo");
        systemInfo.Setup(s => s.GetNetworkAddresses()).Returns([]);
        systemInfo.Setup(s => s.GetStorageUsage()).Returns(pools.ToList());
        systemInfo.Setup(s => s.GetMemoryUsage()).Returns(new MemoryUsageInfo(100, 100, 50, 1_000));
        return systemInfo;
    }

    private static PoolUsageService FixedPoolUsage(Dictionary<string, long> kaimoBytesByPool)
        => new((pool, _) => new PoolUsageBreakdown(kaimoBytesByPool[pool], 0, 0, 0, DateTime.UtcNow));

    [Fact]
    public async Task StorageBreakdown_PoolsOnSameDisk_SubtractEveryPoolFromUsed()
    {
        var p1 = new StorageUsageInfo("/data/storage/pool01", "/data/storage/pool01", 10_000, 6_000, 4_000, true) { DeviceId = "8:1" };
        var p2 = new StorageUsageInfo("/data/storage/pool02", "/data/storage/pool02", 10_000, 6_000, 4_000, true) { DeviceId = "8:1" };
        var p3 = new StorageUsageInfo("/data/storage/pool03", "/data/storage/pool03", 5_000, 1_000, 4_000, true) { DeviceId = "8:17" };
        var h = new Harness(false, canManageSettings: true, systemInfo: SystemInfoWith(p1, p2, p3).Object,
            poolUsage: FixedPoolUsage(new() { [p1.StoragePath] = 1_000, [p2.StoragePath] = 2_000, [p3.StoragePath] = 400 }));
        await h.Vm.LoadAsync();

        await h.Vm.RefreshStorageBreakdownAsync();

        Assert.Equal(2_000, h.Vm.OtherPoolsOnDiskBytes(p1));
        Assert.Equal(1_000, h.Vm.OtherPoolsOnDiskBytes(p2));
        Assert.Equal(3_000, h.Vm.OutsideKaimoBytes(p1));
        Assert.Equal(3_000, h.Vm.OutsideKaimoBytes(p2));
        Assert.Equal(0, h.Vm.OtherPoolsOnDiskBytes(p3));
        Assert.Equal(600, h.Vm.OutsideKaimoBytes(p3));
    }

    [Fact]
    public async Task StorageBreakdown_PoolWithoutDeviceId_StandsAlone_AndOutsideIsClampedAtZero()
    {
        var p1 = new StorageUsageInfo("/a", "/a", 1_000, 500, 500, true);
        var p2 = new StorageUsageInfo("/b", "/b", 1_000, 500, 500, true);
        var h = new Harness(false, canManageSettings: true, systemInfo: SystemInfoWith(p1, p2).Object,
            poolUsage: FixedPoolUsage(new() { ["/a"] = 900, ["/b"] = 100 }));
        await h.Vm.LoadAsync();

        await h.Vm.RefreshStorageBreakdownAsync();

        Assert.Equal(0, h.Vm.OtherPoolsOnDiskBytes(p1));
        Assert.Equal(0, h.Vm.OutsideKaimoBytes(p1));
        Assert.Equal(400, h.Vm.OutsideKaimoBytes(p2));
    }

    [Fact]
    public async Task StorageBreakdown_BeforeScan_IsUnknown_AndCachedResultIsReused()
    {
        var p1 = new StorageUsageInfo("/a", "/a", 1_000, 500, 500, true);
        var calls = 0;
        var usage = new PoolUsageService((_, _) =>
            new PoolUsageBreakdown(100 * Interlocked.Increment(ref calls), 0, 0, 0, DateTime.UtcNow));
        var h = new Harness(false, canManageSettings: true, systemInfo: SystemInfoWith(p1).Object, poolUsage: usage);
        await h.Vm.LoadAsync();

        Assert.Null(h.Vm.OutsideKaimoBytes(p1));

        await h.Vm.RefreshStorageBreakdownAsync();
        await h.Vm.RefreshStorageBreakdownAsync();
        Assert.Equal(1, calls);
        Assert.Equal(400, h.Vm.OutsideKaimoBytes(p1));

        await h.Vm.RefreshStorageBreakdownAsync(force: true);
        Assert.Equal(2, calls);
        Assert.Equal(300, h.Vm.OutsideKaimoBytes(p1));
    }

    [Fact]
    public async Task StorageBreakdown_WithoutSettingsPermission_DoesNotScan()
    {
        var calls = 0;
        var usage = new PoolUsageService((_, _) =>
        {
            Interlocked.Increment(ref calls);
            return new PoolUsageBreakdown(0, 0, 0, 0, DateTime.UtcNow);
        });
        var h = new Harness(canManageDataServices: true,
            systemInfo: SystemInfoWith(new StorageUsageInfo("/a", "/a", 1, 1, 0, true)).Object, poolUsage: usage);
        await h.Vm.LoadAsync();

        await h.Vm.RefreshStorageBreakdownAsync(force: true);

        Assert.Equal(0, calls);
        Assert.Empty(h.Vm.PoolBreakdowns);
    }

    [Fact]
    public async Task StorageSettings_OriginDetails_AreCollapsedByDefault_AndScanOnlyWhenOpened()
    {
        var pool = new StorageUsageInfo("/a", "/a", 1_000, 500, 500, true);
        var calls = 0;
        var usage = new PoolUsageService((_, _) =>
        {
            Interlocked.Increment(ref calls);
            return new PoolUsageBreakdown(100, 0, 0, 0, DateTime.UtcNow);
        });
        var h = new Harness(false, canManageSettings: true, systemInfo: SystemInfoWith(pool).Object, poolUsage: usage);
        await h.Vm.LoadAsync();

        using var ctx = new Bunit.BunitContext();
        var env = new Mock<Microsoft.AspNetCore.Hosting.IWebHostEnvironment>();
        env.SetupGet(e => e.WebRootPath).Returns(MailNotificationTests.FindWebRoot());
        ctx.Services.AddSingleton(new AssetProvider(env.Object));
        ctx.Services.AddSingleton(new DateFormatService(h.Config.Object));
        var cut = ctx.Render<Kaimo_File_Server.Web.Components.Pages.Settings.Components.StorageSettings>(
            p => p.Add(c => c.VM, h.Vm));

        // Collapsed: no details and no pool walk until the admin asks for them.
        Assert.Equal("false", cut.Find(".storage-origin-toggle").GetAttribute("aria-expanded"));
        Assert.Empty(cut.FindAll(".storage-origin-legend"));
        Assert.Equal(0, calls);

        cut.Find(".storage-origin-toggle").Click();
        cut.WaitForAssertion(() => Assert.Single(cut.FindAll(".storage-origin-legend")), TimeSpan.FromSeconds(5));
        Assert.Equal("true", cut.Find(".storage-origin-toggle").GetAttribute("aria-expanded"));
        Assert.Equal(1, calls);

        cut.Find(".storage-origin-toggle").Click();
        cut.WaitForAssertion(() => Assert.Empty(cut.FindAll(".storage-origin-legend")), TimeSpan.FromSeconds(5));
        Assert.Equal("false", cut.Find(".storage-origin-toggle").GetAttribute("aria-expanded"));
        Assert.Equal(1, calls);
    }

    [Theory]
    [InlineData("Web_Settings_Storage_UsedPercent")]
    [InlineData("Web_Settings_Storage_Origin_Label")]
    [InlineData("Web_Settings_Storage_Origin_Hint")]
    [InlineData("Web_Settings_Storage_Origin_Files")]
    [InlineData("Web_Settings_Storage_Origin_RecycleBins")]
    [InlineData("Web_Settings_Storage_Origin_Versions")]
    [InlineData("Web_Settings_Storage_Origin_OtherServer")]
    [InlineData("Web_Settings_Storage_Origin_OtherPools")]
    [InlineData("Web_Settings_Storage_Origin_Outside")]
    [InlineData("Web_Settings_Storage_Origin_Calculating")]
    [InlineData("Web_Settings_Storage_Origin_Unavailable")]
    [InlineData("Web_Settings_Storage_Origin_CalculatedAt")]
    public void StorageOriginKeys_ExistInBothCultures(string key)
        => SettingsMessageKeys_ExistInBothCultures(key);

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

    [Theory]
    [InlineData("en", "Session check saved (every 120s).")]
    [InlineData("de", "Sitzungsprüfung gespeichert (alle 120s).")]
    public async Task SaveSessionSecurityAsync_SuccessMessage_IsLocalized(string culture, string expected)
    {
        var previous = System.Globalization.CultureInfo.CurrentUICulture;
        System.Globalization.CultureInfo.CurrentUICulture = new System.Globalization.CultureInfo(culture);
        try
        {
            var h = new Harness(canManageDataServices: false, canManageSettings: true);
            await h.Vm.LoadAsync();
            h.Vm.SessionRevalidationSeconds = 120;

            Assert.True(await h.Vm.SaveSessionSecurityAsync());
            Assert.Equal(expected, h.Vm.SuccessMessage);
        }
        finally
        {
            System.Globalization.CultureInfo.CurrentUICulture = previous;
        }
    }

    /// <summary>Every message key used by the Certificate/Security settings exists in both cultures.</summary>
    [Theory]
    [InlineData("Web_Settings_Cert_SettingsSaved")]
    [InlineData("Web_Settings_Cert_SettingsSaveFailed")]
    [InlineData("Web_Settings_Cert_Regenerated")]
    [InlineData("Web_Settings_Cert_RegenerateFailed")]
    [InlineData("Web_Settings_Cert_NoFileSelected")]
    [InlineData("Web_Settings_Cert_CustomImported")]
    [InlineData("Web_Settings_Cert_CustomImportFailed")]
    [InlineData("Web_Settings_Session_Saved")]
    [InlineData("Web_Settings_Session_SaveFailed")]
    public void SettingsMessageKeys_ExistInBothCultures(string key)
    {
        var rm = Kaimo_File_Server.Core.Language.Resources.ResourceManager;
        var en = rm.GetString(key, System.Globalization.CultureInfo.InvariantCulture);
        var de = rm.GetString(key, new System.Globalization.CultureInfo("de"));

        Assert.False(string.IsNullOrWhiteSpace(en));
        Assert.False(string.IsNullOrWhiteSpace(de));
        Assert.NotEqual(en, de);
    }

    // ─────────────────────────── Test harness ───────────────────────────

    private sealed class Harness
    {
        public Mock<IConfigRepository> Config { get; } = new();
        public Mock<ISearchAdminService> SearchAdmin { get; } = new();
        public Mock<ICloudAccessSettingsStore> CloudAccessSettings { get; } = new();
        public ToastService Toasts { get; } = new();
        public SettingsViewModel Vm { get; }

        /// <summary>The <see cref="SmbProtocolSettings"/> captured on the last SetAsync write.</summary>
        public SmbProtocolSettings? SavedProtocol { get; private set; }

        public Harness(
            bool canManageDataServices,
            SmbProtocolSettings? storedProtocol = null,
            bool canManageSettings = false,
            ISystemInfoService? systemInfo = null,
            PoolUsageService? poolUsage = null)
        {
            storedProtocol ??= SmbProtocolSettings.Default();

            Config.Setup(c => c.GetBoolAsync(It.IsAny<string>(), It.IsAny<bool>()))
                .ReturnsAsync(true);
            Config.Setup(c => c.GetAsync(SmbProtocolSettings.ConfigKey, It.IsAny<SmbProtocolSettings>()))
                .ReturnsAsync(storedProtocol);
            // The CanManageSettings branch of LoadAsync reads these; unstubbed they
            // return null and blow up before RefreshSystemInfo runs. Echo the fallback,
            // matching the real repo when no value is stored.
            Config.Setup(c => c.GetAsync(
                    Kaimo_File_Server.Web.DynamicHelpers.ContextMenuConfig.ConfigKey,
                    It.IsAny<Kaimo_File_Server.Web.DynamicHelpers.ContextMenuConfig>()))
                .ReturnsAsync((string _, Kaimo_File_Server.Web.DynamicHelpers.ContextMenuConfig fb) => fb);
            Config.Setup(c => c.GetAsync(PasswordPolicy.ConfigKey, It.IsAny<PasswordPolicy>()))
                .ReturnsAsync((string _, PasswordPolicy fb) => fb);
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
            CloudAccessSettings.Setup(s => s.GetAsync())
                .ReturnsAsync(CloudAccessRuntimeSettings.Default());
            CloudAccessSettings.Setup(s => s.SetAsync(It.IsAny<CloudAccessRuntimeSettings>()))
                .Returns(Task.CompletedTask);

            Vm = new SettingsViewModel(
                Config.Object,
                mgmt.Object,
                userFactory.Object,
                new StubAuthProvider("admin"),
                systemInfo ?? Mock.Of<ISystemInfoService>(),
                SearchAdmin.Object,
                Mock.Of<IShareRepository>(),
                Mock.Of<IHttpsCertificateProvider>(),
                loggingStore.Object,
                new Kaimo_File_Server.Infrastructure.Logging.LoggingLevelConfigurationSource(),
                CloudAccessSettings.Object,
                Mock.Of<Kaimo_File_Server.Infrastructure.Backup.IBackupSettingsStore>(),
                Mock.Of<Kaimo_File_Server.Infrastructure.Backup.IDatabaseBackupService>(),
                new Kaimo_File_Server.Web.Services.BackupDownloadTokenService(
                    Microsoft.AspNetCore.DataProtection.DataProtectionProvider.Create("KaimoTests")),
                NullLogger<SettingsViewModel>.Instance,
                poolUsage: poolUsage,
                toasts: Toasts);
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

using Kaimo_File_Server.Core.Logging;
using Kaimo_File_Server.Core.Security.Authentication;
using Kaimo_File_Server.Core.Services.DataServices;
using Kaimo_File_Server.Infrastructure.Configuration;
using Kaimo_File_Server.Infrastructure.Logging;
using Kaimo_File_Server.SmbBridge.Grpc;
using Kaimo_File_Server.SmbBridge.Services;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Kaimo_File_Server.SmbBridge.Tests.Services;

/// <summary>
/// The web UI writes the SMB lockout values and allowed networks in another process, so the
/// bridge must not serve them from its own config cache (10 minutes) to the Samba config sync.
/// Two <see cref="ConfigRepository"/> instances with separate caches stand in for Web and bridge.
/// </summary>
public sealed class SmbProtocolSettingsFreshnessTests : DatabaseTestBase
{
    private ConfigRepository NewProcess() => new(DbFactory, new MemoryCache(new MemoryCacheOptions()));

    [Fact]
    public async Task GetProtocolSettings_SeesLockoutValuesTheWebChangedAfterAnEarlierCall()
    {
        var web = NewProcess();
        var bridge = NewProcess();
        await web.SetAsync(LoginProtectionSettings.SmbLockoutAttemptsKey, 10);
        await web.SetAsync(LoginProtectionSettings.SmbLockoutMinutesKey, 15);
        await web.SetAsync(LoginProtectionSettings.SmbLockoutEnabledKey, true);
        Assert.Equal(10, (await CallAsync(bridge)).LockoutAttempts);

        await web.SetAsync(LoginProtectionSettings.SmbLockoutAttemptsKey, 3);
        await web.SetAsync(LoginProtectionSettings.SmbLockoutMinutesKey, 1);
        await web.SetAsync(LoginProtectionSettings.SmbLockoutEnabledKey, false);
        var reply = await CallAsync(bridge);

        Assert.Equal(3, reply.LockoutAttempts);
        Assert.Equal(1, reply.LockoutMinutes);
        Assert.False(reply.LockoutEnabled);
    }

    [Fact]
    public async Task GetProtocolSettings_SeesAllowedNetworksTheWebChangedAfterAnEarlierCall()
    {
        var web = NewProcess();
        var bridge = NewProcess();
        await web.SetAsync(AccessRestrictionSettings.ConfigKey, new AccessRestrictionSettings { SmbAllowedNetworks = ["10.0.0.0/8"] });
        Assert.Equal(["10.0.0.0/8"], (await CallAsync(bridge)).HostsAllow);

        await web.SetAsync(AccessRestrictionSettings.ConfigKey, new AccessRestrictionSettings { SmbAllowedNetworks = ["192.168.1.0/24"] });

        Assert.Equal(["192.168.1.0/24"], (await CallAsync(bridge)).HostsAllow);
    }

    private static Task<ProtocolSettingsReply> CallAsync(IConfigRepository config)
    {
        var smb = new Mock<ISmbConfigStore>();
        smb.Setup(s => s.GetProtocolSettingsAsync()).ReturnsAsync(new SmbProtocolSettings());
        smb.Setup(s => s.IsSmbEnabledAsync()).ReturnsAsync(true);
        var logging = new Mock<ILoggingConfigStore>();
        logging.Setup(l => l.GetLevelAsync()).ReturnsAsync(LoggingConfigKeys.DefaultLevel);
        var sut = new ConfigGrpcService(
            smb.Object, config, logging.Object, new LoggingLevelConfigurationSource(),
            NullLogger<ConfigGrpcService>.Instance);
        return sut.GetProtocolSettings(new GetProtocolSettingsRequest(), null!);
    }
}

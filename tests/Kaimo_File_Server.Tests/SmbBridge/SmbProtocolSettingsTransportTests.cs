using System.Text.Json;
using Grpc.Core;
using Kaimo_File_Server.Core.Logging;
using Kaimo_File_Server.Core.Security;
using Kaimo_File_Server.Core.Services.DataServices;
using Kaimo_File_Server.Infrastructure.Configuration;
using Kaimo_File_Server.Infrastructure.Logging;
using Kaimo_File_Server.SmbBridge.Grpc;
using Kaimo_File_Server.SmbBridge.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Kaimo_File_Server.Tests;

/// <summary>
/// SMB account lockout and <c>hosts allow</c> values that the bridge hands to the Samba config
/// sync (AP8a): source keys, normalization, invalid networks and the recovery switch.
/// </summary>
public class SmbProtocolSettingsTransportTests
{
    [Fact]
    public void SmbHostsAllow_KeepsValidNetworksNormalizedAndDropsInvalidOnes()
    {
        var settings = new AccessRestrictionSettings
        {
            SmbAllowedNetworks = ["10.0.0.0/8", " 192.168.1.20 ", "2001:db8::/32", "not-a-network", "10.1.2.3/8", "", "10.0.0.0/8"],
        };

        Assert.Equal(["10.0.0.0/8", "192.168.1.20/32", "2001:db8::/32"], settings.SmbHostsAllow());
    }

    // sync-config.sh accepts only dotted IPv4 or colon-only IPv6 CIDRs and at most 256 entries;
    // anything else fails every config cycle, which closes all SMB sessions each time.
    [Fact]
    public void SmbHostsAllow_SendsIPv4MappedRangesAsIPv4()
    {
        var settings = new AccessRestrictionSettings { SmbAllowedNetworks = ["::ffff:192.168.0.0/112", "::ffff:10.0.0.5"] };

        Assert.Equal(["192.168.0.0/16", "10.0.0.5/32"], settings.SmbHostsAllow());
    }

    [Fact]
    public void SmbHostsAllow_SendsAtMostTheLimit()
    {
        var settings = new AccessRestrictionSettings
        {
            SmbAllowedNetworks = Enumerable.Range(0, AccessRestrictionSettings.MaxSmbAllowedNetworks + 10)
                .Select(i => $"10.{i / 256}.{i % 256}.0/24").ToList(),
        };

        Assert.Equal(AccessRestrictionSettings.MaxSmbAllowedNetworks, settings.SmbHostsAllow().Count);
    }

    [Fact]
    public void SmbAllowedNetworks_MissingInStoredJson_IsEmpty()
    {
        // A blob saved before AP8 has no SmbAllowedNetworks property.
        var settings = JsonSerializer.Deserialize<AccessRestrictionSettings>("""{"Rules":[]}""")!;

        Assert.Empty(settings.SmbAllowedNetworks);
        Assert.Empty(settings.SmbHostsAllow());
    }

    [Fact]
    public async Task GetProtocolSettings_DefaultsSendLockoutOnWithTenAttemptsAndFifteenMinutes()
    {
        var reply = await CallAsync(Config());

        Assert.True(reply.LockoutEnabled);
        Assert.Equal(10, reply.LockoutAttempts);
        Assert.Equal(15, reply.LockoutMinutes);
        Assert.Empty(reply.HostsAllow);
    }

    [Fact]
    public async Task GetProtocolSettings_SendsStoredLockoutValuesNormalized()
    {
        var config = Config();
        config.Setup(c => c.GetFreshAsync(LoginProtectionSettings.SmbLockoutEnabledKey, It.IsAny<bool>())).ReturnsAsync(false);
        config.Setup(c => c.GetFreshAsync(LoginProtectionSettings.SmbLockoutAttemptsKey, It.IsAny<int>())).ReturnsAsync(3);
        config.Setup(c => c.GetFreshAsync(LoginProtectionSettings.SmbLockoutMinutesKey, It.IsAny<int>())).ReturnsAsync(1_000_000);

        var reply = await CallAsync(config);

        Assert.False(reply.LockoutEnabled);
        Assert.Equal(3, reply.LockoutAttempts);
        Assert.Equal(LoginProtectionSettings.MaxLockoutMinutes, reply.LockoutMinutes);
    }

    [Fact]
    public async Task GetProtocolSettings_SendsOnlyValidAllowedNetworks()
    {
        var config = Config(new AccessRestrictionSettings { SmbAllowedNetworks = ["10.0.0.0/8", "bogus", "fd00::/8"] });

        var reply = await CallAsync(config);

        Assert.Equal(["10.0.0.0/8", "fd00::/8"], reply.HostsAllow);
    }

    internal static Mock<IConfigRepository> Config(AccessRestrictionSettings? restrictions = null)
    {
        var config = new Mock<IConfigRepository>();
        config.Setup(c => c.GetFreshAsync(It.IsAny<string>(), It.IsAny<int>()))
            .Returns((string _, int fallback) => Task.FromResult(fallback));
        config.Setup(c => c.GetFreshAsync(It.IsAny<string>(), It.IsAny<bool>()))
            .Returns((string _, bool fallback) => Task.FromResult(fallback));
        config.Setup(c => c.GetFreshAsync(AccessRestrictionSettings.ConfigKey, It.IsAny<AccessRestrictionSettings>()))
            .ReturnsAsync(restrictions ?? AccessRestrictionSettings.Default());
        return config;
    }

    internal static Task<ProtocolSettingsReply> CallAsync(Mock<IConfigRepository> config)
    {
        var smb = new Mock<ISmbConfigStore>();
        smb.Setup(s => s.GetProtocolSettingsAsync()).ReturnsAsync(new SmbProtocolSettings());
        smb.Setup(s => s.IsSmbEnabledAsync()).ReturnsAsync(true);
        var logging = new Mock<ILoggingConfigStore>();
        logging.Setup(l => l.GetLevelAsync()).ReturnsAsync(LoggingConfigKeys.DefaultLevel);
        var sut = new ConfigGrpcService(
            smb.Object, config.Object, logging.Object, new LoggingLevelConfigurationSource(),
            NullLogger<ConfigGrpcService>.Instance);
        return sut.GetProtocolSettings(new GetProtocolSettingsRequest(), null!);
    }
}

[CollectionDefinition(nameof(AccessRestrictionEnvironmentCollection), DisableParallelization = true)]
public sealed class AccessRestrictionEnvironmentCollection;

/// <summary>The recovery switch also lifts the SMB <c>hosts allow</c> list (process-wide variable, so serial).</summary>
[Collection(nameof(AccessRestrictionEnvironmentCollection))]
public class SmbHostsAllowRecoverySwitchTests
{
    [Fact]
    public async Task GetProtocolSettings_RecoverySwitchSendsNoAllowedNetworks()
    {
        var config = SmbProtocolSettingsTransportTests.Config(
            new AccessRestrictionSettings { SmbAllowedNetworks = ["10.0.0.0/8"] });
        Environment.SetEnvironmentVariable(AccessRestrictionSettings.DisableEnvironmentVariable, "true");
        try
        {
            var reply = await SmbProtocolSettingsTransportTests.CallAsync(config);

            Assert.Empty(reply.HostsAllow);
        }
        finally
        {
            Environment.SetEnvironmentVariable(AccessRestrictionSettings.DisableEnvironmentVariable, null);
        }
    }
}

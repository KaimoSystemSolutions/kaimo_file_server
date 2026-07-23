using Kaimo_File_Server.SmbBridge.Security;
using Microsoft.Extensions.Options;
using Xunit;

namespace Kaimo_File_Server.Tests;

public sealed class ControlPlaneSecurityTests
{
    [Fact]
    public void SyncIdentities_AreRestrictedToTheirSingleRpcGroup()
    {
        Assert.True(ControlPlaneAccessPolicy.IsAllowed(
            ControlPlaneAccessPolicy.AuthSyncClient,
            "/kaimo.smb.bridge.v1.AuthService/ListUsers"));
        Assert.True(ControlPlaneAccessPolicy.IsAllowed(
            ControlPlaneAccessPolicy.ShareSyncClient,
            "/kaimo.smb.bridge.v1.ShareService/ListShares"));
        Assert.True(ControlPlaneAccessPolicy.IsAllowed(
            ControlPlaneAccessPolicy.ConfigSyncClient,
            "/kaimo.smb.bridge.v1.ConfigService/GetProtocolSettings"));

        Assert.False(ControlPlaneAccessPolicy.IsAllowed(
            ControlPlaneAccessPolicy.AuthSyncClient,
            "/kaimo.smb.bridge.v1.SnapshotService/ResolveVersion"));
        Assert.False(ControlPlaneAccessPolicy.IsAllowed(
            ControlPlaneAccessPolicy.ShareSyncClient,
            "/kaimo.smb.bridge.v1.EventService/NotifyDelete"));
        Assert.False(ControlPlaneAccessPolicy.IsAllowed(
            ControlPlaneAccessPolicy.ConfigSyncClient,
            "/kaimo.smb.bridge.v1.AuthzService/AuthorizeOpen"));
    }

    [Fact]
    public void RuntimeIdentity_CannotExportNtHashes()
    {
        Assert.True(ControlPlaneAccessPolicy.IsAllowed(
            ControlPlaneAccessPolicy.RuntimeClient,
            "/kaimo.smb.bridge.v1.AuthzService/AuthorizeConnect"));
        Assert.True(ControlPlaneAccessPolicy.IsAllowed(
            ControlPlaneAccessPolicy.RuntimeClient,
            "/kaimo.smb.bridge.v1.EventService/NotifyClose"));
        Assert.True(ControlPlaneAccessPolicy.IsAllowed(
            ControlPlaneAccessPolicy.RuntimeClient,
            "/kaimo.smb.bridge.v1.SnapshotService/EnumerateSnapshots"));

        Assert.False(ControlPlaneAccessPolicy.IsAllowed(
            ControlPlaneAccessPolicy.RuntimeClient,
            "/kaimo.smb.bridge.v1.AuthService/ListUsers"));
        Assert.False(ControlPlaneAccessPolicy.IsAllowed(
            ControlPlaneAccessPolicy.RuntimeClient,
            "/kaimo.smb.bridge.v1.AuthService/GetNtHash"));
    }

    [Fact]
    public void UnknownClient_AndUnassignedGetNtHash_AreDenied()
    {
        Assert.False(ControlPlaneAccessPolicy.IsKnownClient("unrelated-container"));
        Assert.False(ControlPlaneAccessPolicy.IsAllowed(
            "unrelated-container",
            "/kaimo.smb.bridge.v1.AuthService/ListUsers"));
        Assert.False(ControlPlaneAccessPolicy.IsAllowed(
            ControlPlaneAccessPolicy.AuthSyncClient,
            "/kaimo.smb.bridge.v1.AuthService/GetNtHash"));
    }

    [Fact]
    public void HashExportRateLimiter_RejectsExcessAndResetsAfterWindow()
    {
        var clock = new MutableTimeProvider(
            new DateTimeOffset(2026, 7, 23, 10, 0, 0, TimeSpan.Zero));
        var limiter = new HashExportRateLimiter(
            Options.Create(new HashExportRateLimitOptions
            {
                PermitLimit = 2,
                WindowSeconds = 60,
            }),
            clock);

        Assert.True(limiter.TryAcquire(ControlPlaneAccessPolicy.AuthSyncClient, out _));
        Assert.True(limiter.TryAcquire(ControlPlaneAccessPolicy.AuthSyncClient, out _));
        Assert.False(limiter.TryAcquire(
            ControlPlaneAccessPolicy.AuthSyncClient,
            out TimeSpan retryAfter));
        Assert.Equal(TimeSpan.FromSeconds(60), retryAfter);

        clock.Advance(TimeSpan.FromSeconds(60));

        Assert.True(limiter.TryAcquire(ControlPlaneAccessPolicy.AuthSyncClient, out _));
    }

    private sealed class MutableTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        private DateTimeOffset _utcNow = utcNow;

        public override DateTimeOffset GetUtcNow() => _utcNow;

        public void Advance(TimeSpan duration) => _utcNow += duration;
    }
}

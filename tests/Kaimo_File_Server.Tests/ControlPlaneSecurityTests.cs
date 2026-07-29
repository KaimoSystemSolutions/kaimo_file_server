using Kaimo_File_Server.SmbBridge.Security;
using Microsoft.Extensions.Options;
using Xunit;

namespace Kaimo_File_Server.Tests;

public sealed class ControlPlaneSecurityTests
{
    [Fact]
    public void SambaIdentity_CanCallEveryRequiredRpcGroup()
    {
        Assert.True(ControlPlaneAccessPolicy.IsAllowed(
            ControlPlaneAccessPolicy.SambaClient,
            "/kaimo.smb.bridge.v1.AuthService/ListUsers"));
        Assert.True(ControlPlaneAccessPolicy.IsAllowed(
            ControlPlaneAccessPolicy.SambaClient,
            "/kaimo.smb.bridge.v1.ShareService/ListShares"));
        Assert.True(ControlPlaneAccessPolicy.IsAllowed(
            ControlPlaneAccessPolicy.SambaClient,
            "/kaimo.smb.bridge.v1.ConfigService/GetProtocolSettings"));
        Assert.True(ControlPlaneAccessPolicy.IsAllowed(
            ControlPlaneAccessPolicy.SambaClient,
            "/kaimo.smb.bridge.v1.AuthzService/AuthorizeConnect"));
        Assert.True(ControlPlaneAccessPolicy.IsAllowed(
            ControlPlaneAccessPolicy.SambaClient,
            "/kaimo.smb.bridge.v1.EventService/NotifyClose"));
        Assert.True(ControlPlaneAccessPolicy.IsAllowed(
            ControlPlaneAccessPolicy.SambaClient,
            "/kaimo.smb.bridge.v1.SnapshotService/EnumerateSnapshots"));

        Assert.False(ControlPlaneAccessPolicy.IsAllowed(
            ControlPlaneAccessPolicy.SambaClient,
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
            ControlPlaneAccessPolicy.SambaClient,
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

        Assert.True(limiter.TryAcquire(ControlPlaneAccessPolicy.SambaClient, out _));
        Assert.True(limiter.TryAcquire(ControlPlaneAccessPolicy.SambaClient, out _));
        Assert.False(limiter.TryAcquire(
            ControlPlaneAccessPolicy.SambaClient,
            out TimeSpan retryAfter));
        Assert.Equal(TimeSpan.FromSeconds(60), retryAfter);

        clock.Advance(TimeSpan.FromSeconds(60));

        Assert.True(limiter.TryAcquire(ControlPlaneAccessPolicy.SambaClient, out _));
    }

    [Fact]
    public void HashExportContinuationToken_IsBoundToClientAndOffset()
    {
        var clock = new MutableTimeProvider(
            new DateTimeOffset(2026, 7, 29, 12, 0, 0, TimeSpan.Zero));
        var limiter = new HashExportRateLimiter(
            Options.Create(new HashExportRateLimitOptions()),
            clock);
        string token = limiter.CreateContinuationToken("kaimo-samba", 1_000);

        Assert.True(limiter.IsValidContinuationToken(
            "kaimo-samba", 1_000, token));
        Assert.False(limiter.IsValidContinuationToken(
            "other-client", 1_000, token));
        Assert.False(limiter.IsValidContinuationToken(
            "kaimo-samba", 2_000, token));
        Assert.False(limiter.IsValidContinuationToken(
            "kaimo-samba", 1_000, token + "tampered"));

        clock.Advance(TimeSpan.FromMinutes(11));
        Assert.False(limiter.IsValidContinuationToken(
            "kaimo-samba", 1_000, token));
    }

    private sealed class MutableTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        private DateTimeOffset _utcNow = utcNow;

        public override DateTimeOffset GetUtcNow() => _utcNow;

        public void Advance(TimeSpan duration) => _utcNow += duration;
    }
}

using System;
using Kaimo_File_Server.Core.Domain.ClientSync;
using Kaimo_File_Server.Core.Services.Sync;
using Xunit;

namespace Kaimo_File_Server.Tests;

/// <summary>
/// Pure-unit tests for the client sync/browse API's transport-agnostic building
/// blocks (no database required).
/// </summary>
public sealed class ClientSyncApiTests
{
    // ─────────────────── ShareChangeState token round-trip ───────────────────

    [Fact]
    public void Change_token_round_trips_through_string()
    {
        var state = new ShareChangeState(new DateTime(2026, 8, 22, 10, 0, 0, DateTimeKind.Utc), 42);
        var parsed = ShareChangeState.FromToken(state.ToToken());

        Assert.Equal(state.MaxModifiedUtc, parsed.MaxModifiedUtc);
        Assert.Equal(state.ItemCount, parsed.ItemCount);
    }

    [Fact]
    public void Empty_change_state_token_round_trips()
    {
        var parsed = ShareChangeState.FromToken(ShareChangeState.Empty.ToToken());
        Assert.Equal(ShareChangeState.Empty, parsed);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not-a-token")]
    [InlineData("123")]          // missing count segment
    [InlineData("abc:def")]      // non-numeric
    public void Malformed_change_token_parses_to_empty(string? token)
    {
        Assert.Equal(ShareChangeState.Empty, ShareChangeState.FromToken(token));
    }

    [Fact]
    public void A_new_item_changes_the_token()
    {
        var before = new ShareChangeState(new DateTime(2026, 8, 22, 10, 0, 0, DateTimeKind.Utc), 5);
        // A create both bumps the newest-modified and the count.
        var afterCreate = new ShareChangeState(new DateTime(2026, 8, 22, 11, 0, 0, DateTimeKind.Utc), 6);
        // A delete drops the count while the newest-modified may be unchanged.
        var afterDelete = new ShareChangeState(before.MaxModifiedUtc, 4);

        Assert.NotEqual(before.ToToken(), afterCreate.ToToken());
        Assert.NotEqual(before.ToToken(), afterDelete.ToToken());
    }

    // ─────────────────── RefreshToken lifecycle ───────────────────

    [Fact]
    public void Active_refresh_token_is_valid_before_expiry_and_not_revoked()
    {
        var now = DateTime.UtcNow;
        var token = new RefreshToken { ExpiresAtUtc = now.AddDays(1) };
        Assert.True(token.IsActive(now));
    }

    [Fact]
    public void Expired_refresh_token_is_inactive()
    {
        var now = DateTime.UtcNow;
        var token = new RefreshToken { ExpiresAtUtc = now.AddSeconds(-1) };
        Assert.False(token.IsActive(now));
    }

    [Fact]
    public void Revoked_refresh_token_is_inactive_even_before_expiry()
    {
        var now = DateTime.UtcNow;
        var token = new RefreshToken { ExpiresAtUtc = now.AddDays(1), RevokedAtUtc = now };
        Assert.False(token.IsActive(now));
    }

    // ─────────────────── SyncDevice revocation ───────────────────

    [Fact]
    public void Device_is_active_until_revoked()
    {
        var device = new SyncDevice();
        Assert.True(device.IsActive);

        device.RevokedAtUtc = DateTime.UtcNow;
        Assert.False(device.IsActive);
    }
}

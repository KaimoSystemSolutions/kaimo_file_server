using System;
using Kaimo_File_Server.Core.Domain;
using Kaimo_File_Server.Core.Domain.ClientSync;
using Kaimo_File_Server.Core.Services.Sync;
using Kaimo_File_Server.Web.Controllers.Api;
using Kaimo_File_Server.Web.Services.Api;
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

    // ─────────────── Refresh reuse grace (concurrent-refresh race) ───────────────

    [Fact]
    public void Recent_rotation_with_live_replacement_is_a_benign_race()
    {
        var now = DateTime.UtcNow;
        Assert.True(ApiTokenService.IsBenignRefreshRace(
            revokedAtUtc: now.AddSeconds(-2),
            replacedByTokenId: Guid.NewGuid(),
            replacementActive: true,
            nowUtc: now,
            grace: TimeSpan.FromSeconds(10)));
    }

    [Fact]
    public void Replay_after_the_grace_window_is_treated_as_theft()
    {
        var now = DateTime.UtcNow;
        Assert.False(ApiTokenService.IsBenignRefreshRace(
            revokedAtUtc: now.AddSeconds(-30),
            replacedByTokenId: Guid.NewGuid(),
            replacementActive: true,
            nowUtc: now,
            grace: TimeSpan.FromSeconds(10)));
    }

    [Fact]
    public void Replay_of_a_logged_out_token_without_replacement_is_theft()
    {
        var now = DateTime.UtcNow;
        // Logout/device-revocation leaves no replacement link → never lenient.
        Assert.False(ApiTokenService.IsBenignRefreshRace(
            revokedAtUtc: now.AddSeconds(-2),
            replacedByTokenId: null,
            replacementActive: false,
            nowUtc: now,
            grace: TimeSpan.FromSeconds(10)));
    }

    [Fact]
    public void Replay_when_replacement_already_dead_is_theft()
    {
        var now = DateTime.UtcNow;
        // The chain was already compromised/revoked → re-revoke, don't be lenient.
        Assert.False(ApiTokenService.IsBenignRefreshRace(
            revokedAtUtc: now.AddSeconds(-2),
            replacedByTokenId: Guid.NewGuid(),
            replacementActive: false,
            nowUtc: now,
            grace: TimeSpan.FromSeconds(10)));
    }

    [Fact]
    public void Zero_grace_disables_leniency()
    {
        var now = DateTime.UtcNow;
        Assert.False(ApiTokenService.IsBenignRefreshRace(
            revokedAtUtc: now.AddSeconds(-1),
            replacedByTokenId: Guid.NewGuid(),
            replacementActive: true,
            nowUtc: now,
            grace: TimeSpan.Zero));
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

    // ─────────────────── ItemTag (conditional-op validator) ───────────────────

    private static FileMetadata Meta(long size, DateTime modified, bool dir = false) =>
        new() { Size = size, ModifiedAt = modified, IsDirectory = dir, Path = "x", Name = "x" };

    [Fact]
    public void Item_tag_is_derived_from_size_and_modified_ticks()
    {
        var when = new DateTime(2026, 8, 23, 12, 0, 0, DateTimeKind.Utc);
        var tag = ItemTag.For(Meta(1234, when));
        Assert.Equal($"\"1234:{when.Ticks}\"", tag);
    }

    [Fact]
    public void Item_tag_changes_when_size_or_mtime_changes()
    {
        var when = new DateTime(2026, 8, 23, 12, 0, 0, DateTimeKind.Utc);
        var baseTag = ItemTag.For(Meta(10, when));
        Assert.NotEqual(baseTag, ItemTag.For(Meta(11, when)));
        Assert.NotEqual(baseTag, ItemTag.For(Meta(10, when.AddSeconds(1))));
    }

    [Fact]
    public void Item_tag_is_computed_in_utc_regardless_of_input_kind()
    {
        var utc = new DateTime(2026, 8, 23, 12, 0, 0, DateTimeKind.Utc);
        var local = utc.ToLocalTime(); // same instant, Local kind
        Assert.Equal(ItemTag.For(Meta(5, utc)), ItemTag.For(Meta(5, local)));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Empty_if_match_is_treated_as_no_condition(string? header)
    {
        Assert.True(ItemTag.Matches(header, Meta(1, DateTime.UnixEpoch)));
    }

    [Fact]
    public void Star_if_match_matches_any_existing_item()
    {
        Assert.True(ItemTag.Matches("*", Meta(99, DateTime.UtcNow)));
    }

    [Fact]
    public void If_match_matches_only_the_exact_tag()
    {
        var m = Meta(42, new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        var tag = ItemTag.For(m);

        Assert.True(ItemTag.Matches(tag, m));
        Assert.True(ItemTag.Matches($"\"deadbeef\", {tag}", m)); // one of a list matches
        Assert.True(ItemTag.Matches($"W/{tag}", m));             // weak-validator prefix tolerated
        Assert.False(ItemTag.Matches("\"1:2\"", m));             // a different tag does not match
    }

    // ─────────────────── Idempotency fingerprint & decision ───────────────────

    [Fact]
    public void Fingerprint_is_stable_for_the_same_request()
    {
        var a = RequestFingerprint.Compute("POST", "share/rename", "a.txt", "b.txt");
        var b = RequestFingerprint.Compute("POST", "share/rename", "a.txt", "b.txt");
        Assert.Equal(a, b);
    }

    [Fact]
    public void Fingerprint_differs_when_any_part_differs()
    {
        var baseline = RequestFingerprint.Compute("POST", "share/rename", "a.txt", "b.txt");
        Assert.NotEqual(baseline, RequestFingerprint.Compute("DELETE", "share/rename", "a.txt", "b.txt"));
        Assert.NotEqual(baseline, RequestFingerprint.Compute("POST", "share/rename", "a.txt", "c.txt"));
    }

    [Fact]
    public void Fingerprint_has_no_boundary_ambiguity_between_parts()
    {
        // Length-prefixing means ("ab","c") and ("a","bc") must not collide.
        var x = RequestFingerprint.Compute("POST", "p", "ab", "c");
        var y = RequestFingerprint.Compute("POST", "p", "a", "bc");
        Assert.NotEqual(x, y);
    }

    [Fact]
    public void Idempotency_decides_proceed_replay_or_conflict()
    {
        const string hash = "abc";
        Assert.Equal(IdempotencyOutcome.Proceed, IdempotencyDecision.Decide(null, hash));
        Assert.Equal(IdempotencyOutcome.Replay, IdempotencyDecision.Decide(hash, hash));
        Assert.Equal(IdempotencyOutcome.Conflict, IdempotencyDecision.Decide("other", hash));
    }
}

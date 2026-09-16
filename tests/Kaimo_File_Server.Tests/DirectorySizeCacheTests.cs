using Kaimo_File_Server.Web.Services;
using Xunit;

namespace Kaimo_File_Server.Tests;

/// <summary>
/// Tests for <see cref="DirectorySizeCache"/> — the TTL-bounded, capacity-bounded
/// memoisation of directory sizes (plan Phase 5).
/// </summary>
public class DirectorySizeCacheTests
{
    private sealed class MutableTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        private DateTimeOffset _utcNow = utcNow;
        public override DateTimeOffset GetUtcNow() => _utcNow;
        public void Advance(TimeSpan duration) => _utcNow += duration;
    }

    private static MutableTimeProvider Clock() => new(DateTimeOffset.UnixEpoch);

    [Fact]
    public void Set_ThenTryGet_ReturnsValue()
    {
        var cache = new DirectorySizeCache(Clock());
        var share = Guid.NewGuid();

        cache.Set(share, "docs/reports", 4096);

        Assert.True(cache.TryGet(share, "docs/reports", out var size));
        Assert.Equal(4096, size);
    }

    [Fact]
    public void TryGet_AfterTtl_ReturnsFalse()
    {
        var clock = Clock();
        var cache = new DirectorySizeCache(clock);
        var share = Guid.NewGuid();
        cache.Set(share, "docs", 100);

        clock.Advance(DirectorySizeCache.DefaultTtl + TimeSpan.FromSeconds(1));

        Assert.False(cache.TryGet(share, "docs", out var size));
        Assert.Equal(0, size);
    }

    [Fact]
    public void TryGet_DifferentShareSamePath_DoesNotCollide()
    {
        var cache = new DirectorySizeCache(Clock());
        var shareA = Guid.NewGuid();
        var shareB = Guid.NewGuid();
        cache.Set(shareA, "docs", 100);

        Assert.False(cache.TryGet(shareB, "docs", out _));
    }

    [Fact]
    public void Invalidate_AlsoDropsAncestors()
    {
        var cache = new DirectorySizeCache(Clock());
        var share = Guid.NewGuid();
        cache.Set(share, "", 1000);            // share root
        cache.Set(share, "a", 500);
        cache.Set(share, "a/b", 200);
        cache.Set(share, "a/b/c", 50);
        cache.Set(share, "other", 9);

        // A write under a/b/c must invalidate a/b/c and every ancestor (a/b, a, root).
        cache.Invalidate(share, "a/b/c");

        Assert.False(cache.TryGet(share, "a/b/c", out _));
        Assert.False(cache.TryGet(share, "a/b", out _));
        Assert.False(cache.TryGet(share, "a", out _));
        Assert.False(cache.TryGet(share, "", out _));
        // An unrelated sibling subtree is untouched.
        Assert.True(cache.TryGet(share, "other", out var otherSize));
        Assert.Equal(9, otherSize);
    }

    [Fact]
    public void InvalidateShare_DropsOnlyThatShare()
    {
        var cache = new DirectorySizeCache(Clock());
        var shareA = Guid.NewGuid();
        var shareB = Guid.NewGuid();
        cache.Set(shareA, "docs", 1);
        cache.Set(shareB, "docs", 2);

        cache.InvalidateShare(shareA);

        Assert.False(cache.TryGet(shareA, "docs", out _));
        Assert.True(cache.TryGet(shareB, "docs", out var size));
        Assert.Equal(2, size);
    }

    [Fact]
    public void Set_BeyondCapacity_EvictsOldest()
    {
        // Small capacity via the internal test seam.
        var cache = new DirectorySizeCache(Clock(), maxEntries: 3);
        var share = Guid.NewGuid();

        cache.Set(share, "p0", 0);   // oldest
        cache.Set(share, "p1", 1);
        cache.Set(share, "p2", 2);
        cache.Set(share, "p3", 3);   // overflows -> evicts the oldest (p0)

        Assert.False(cache.TryGet(share, "p0", out _));
        Assert.True(cache.TryGet(share, "p1", out _));
        Assert.True(cache.TryGet(share, "p2", out _));
        Assert.True(cache.TryGet(share, "p3", out _));
    }
}

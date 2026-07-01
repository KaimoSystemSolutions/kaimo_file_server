using Kaimo_File_Server.Core.Domain.Identity;
using Kaimo_File_Server.Core.Security;
using Kaimo_File_Server.Smb;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using Xunit;

namespace Kaimo_File_Server.Tests;

/// <summary>
/// Serialises every test that touches the process-wide static <see cref="KaimoUserRegistry"/>
/// (its clock / DI provider) so xUnit's cross-class parallelism cannot interleave them.
/// </summary>
[CollectionDefinition("KaimoUserRegistrySerial", DisableParallelization = true)]
public sealed class KaimoUserRegistrySerialCollection { }

[Collection("KaimoUserRegistrySerial")]
/// <summary>
/// Tests the TTL / re-resolution behaviour of <see cref="KaimoUserRegistry"/>: cached identities
/// must expire so that revoked permissions and disabled/deleted accounts stop being served stale,
/// while transient DB failures must not lock out live sessions.
///
/// The registry is a process-wide static, so every test resets it via <c>ResetForTests</c> with an
/// isolated clock and DI container.
/// </summary>
public class KaimoUserRegistryTests
{
    private DateTime _now = new(2026, 7, 1, 12, 0, 0, DateTimeKind.Utc);
    private readonly TimeSpan _ttl = TimeSpan.FromSeconds(30);

    private static UserContext MakeContext(string username, Guid? id = null)
    {
        var user = new User(id ?? Guid.NewGuid(), "Display", username, "hash", "nthash");
        return new UserContext(user, [], [], []);
    }

    /// <summary>Builds a DI container whose IAuthenticationLookup is the supplied mock.</summary>
    private static IServiceProvider Provider(Mock<IAuthenticationLookup> auth)
    {
        var services = new ServiceCollection();
        services.AddScoped(_ => auth.Object);
        return services.BuildServiceProvider();
    }

    private void Init(Mock<IAuthenticationLookup> auth)
        => KaimoUserRegistry.ResetForTests(_ttl, () => _now, Provider(auth));

    // ═══════════════════ Fresh cache hit ═══════════════════

    [Fact]
    public void Lookup_FreshEntry_ReturnsCachedWithoutHittingDb()
    {
        var auth = new Mock<IAuthenticationLookup>();
        Init(auth);

        var ctx = MakeContext("alice");
        KaimoUserRegistry.Register(ctx);

        _now = _now.AddSeconds(29); // still within TTL

        Assert.Same(ctx, KaimoUserRegistry.Lookup("alice"));
        auth.Verify(a => a.ResolveUserContextAsync(It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public void Lookup_IsCaseInsensitive()
    {
        var auth = new Mock<IAuthenticationLookup>();
        Init(auth);

        var ctx = MakeContext("Alice");
        KaimoUserRegistry.Register(ctx);

        Assert.Same(ctx, KaimoUserRegistry.Lookup("alice"));
    }

    // ═══════════════════ Expiry → re-resolve ═══════════════════

    [Fact]
    public void Lookup_ExpiredEntry_ReResolvesFreshContext()
    {
        var stale = MakeContext("bob");
        var fresh = MakeContext("bob");

        var auth = new Mock<IAuthenticationLookup>();
        auth.Setup(a => a.ResolveUserContextAsync("bob")).ReturnsAsync(fresh);
        Init(auth);

        KaimoUserRegistry.Register(stale);
        _now = _now.AddSeconds(31); // past TTL

        var result = KaimoUserRegistry.Lookup("bob");

        Assert.Same(fresh, result); // refreshed, not the stale grant
        auth.Verify(a => a.ResolveUserContextAsync("bob"), Times.Once);
    }

    [Fact]
    public void Lookup_AfterRefresh_CachesTheNewContext()
    {
        var fresh = MakeContext("bob");
        var auth = new Mock<IAuthenticationLookup>();
        auth.Setup(a => a.ResolveUserContextAsync("bob")).ReturnsAsync(fresh);
        Init(auth);

        KaimoUserRegistry.Register(MakeContext("bob"));
        _now = _now.AddSeconds(31);

        KaimoUserRegistry.Lookup("bob");         // triggers refresh (+ re-registers with _now)
        var second = KaimoUserRegistry.Lookup("bob"); // within TTL of the refresh → cached

        Assert.Same(fresh, second);
        auth.Verify(a => a.ResolveUserContextAsync("bob"), Times.Once);
    }

    // ═══════════════════ Revocation: disabled / deleted ═══════════════════

    [Fact]
    public void Lookup_ExpiredEntry_UserNoLongerResolvable_EvictsAndDenies()
    {
        var auth = new Mock<IAuthenticationLookup>();
        auth.Setup(a => a.ResolveUserContextAsync("carol")).ReturnsAsync((UserContext?)null);
        Init(auth);

        KaimoUserRegistry.Register(MakeContext("carol"));
        _now = _now.AddSeconds(31);

        Assert.Null(KaimoUserRegistry.Lookup("carol")); // disabled/deleted → denied

        // Entry was evicted: a further lookup re-queries rather than serving the old grant.
        Assert.Null(KaimoUserRegistry.Lookup("carol"));
        auth.Verify(a => a.ResolveUserContextAsync("carol"), Times.Exactly(2));
    }

    // ═══════════════════ Unknown user (enumeration before first op) ═══════════════════

    [Fact]
    public void Lookup_UnknownUser_ResolvesViaDbAndCaches()
    {
        var ctx = MakeContext("dave");
        var auth = new Mock<IAuthenticationLookup>();
        auth.Setup(a => a.ResolveUserContextAsync("dave")).ReturnsAsync(ctx);
        Init(auth);

        var first = KaimoUserRegistry.Lookup("dave");  // never Registered → resolves
        var second = KaimoUserRegistry.Lookup("dave"); // now cached

        Assert.Same(ctx, first);
        Assert.Same(ctx, second);
        auth.Verify(a => a.ResolveUserContextAsync("dave"), Times.Once);
    }

    [Fact]
    public void Lookup_UnknownUser_NotResolvable_ReturnsNull()
    {
        var auth = new Mock<IAuthenticationLookup>();
        auth.Setup(a => a.ResolveUserContextAsync(It.IsAny<string>())).ReturnsAsync((UserContext?)null);
        Init(auth);

        Assert.Null(KaimoUserRegistry.Lookup("ghost"));
    }

    // ═══════════════════ Transient DB error ═══════════════════

    [Fact]
    public void Lookup_ExpiredEntry_TransientDbError_ServesLastKnownContext()
    {
        var known = MakeContext("erin");
        var auth = new Mock<IAuthenticationLookup>();
        auth.Setup(a => a.ResolveUserContextAsync("erin")).ThrowsAsync(new InvalidOperationException("db down"));
        Init(auth);

        KaimoUserRegistry.Register(known);
        _now = _now.AddSeconds(31);

        // Re-resolution fails transiently → keep serving the cached context (downstream ACL
        // checks are still fail-closed if the DB is truly unavailable).
        Assert.Same(known, KaimoUserRegistry.Lookup("erin"));
    }

    [Fact]
    public void Lookup_UnknownUser_TransientDbError_ReturnsNull()
    {
        var auth = new Mock<IAuthenticationLookup>();
        auth.Setup(a => a.ResolveUserContextAsync(It.IsAny<string>())).ThrowsAsync(new InvalidOperationException("db down"));
        Init(auth);

        Assert.Null(KaimoUserRegistry.Lookup("nobody"));
    }

    // ═══════════════════ Guard clauses ═══════════════════

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void Lookup_NullOrEmptyUsername_ReturnsNull(string? username)
    {
        Init(new Mock<IAuthenticationLookup>());
        Assert.Null(KaimoUserRegistry.Lookup(username));
    }

    [Fact]
    public void Register_RefreshesTimestamp_ExtendingLifetime()
    {
        var auth = new Mock<IAuthenticationLookup>();
        Init(auth);

        var ctx = MakeContext("frank");
        KaimoUserRegistry.Register(ctx);

        _now = _now.AddSeconds(20);
        KaimoUserRegistry.Register(ctx); // re-register resets the clock

        _now = _now.AddSeconds(20); // 40s since first register, but only 20s since the second
        Assert.Same(ctx, KaimoUserRegistry.Lookup("frank"));
        auth.Verify(a => a.ResolveUserContextAsync(It.IsAny<string>()), Times.Never);
    }
}

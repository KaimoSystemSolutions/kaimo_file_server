using Kaimo_File_Server.Web.Services;
using Kaimo_File_Server.Web.Services.Auth;
using Kaimo_File_Server.Web.Services.Downloads;
using Kaimo_File_Server.Tests.Common;
using Xunit;

namespace Kaimo_File_Server.Web.Tests.Services;

/// <summary>
/// The shared base of every in-memory, single-use ticket store (file, ZIP, public and Cloud Access
/// downloads, web session handoff). A ticket is a capability, so it must resolve exactly once and
/// never after its lifetime.
/// </summary>
public class OneTimeTicketStoreTests
{
    private sealed class TestStore(TimeSpan lifetime, TimeProvider time)
        : OneTimeTicketStore<string>(lifetime, time);

    [Fact]
    public void IssuedTicket_IsConsumableExactlyOnce()
    {
        var store = new TestStore(TimeSpan.FromMinutes(5), new MutableTimeProvider());
        var token = store.Issue("payload");

        Assert.True(store.TryConsume(token, out var first));
        Assert.Equal("payload", first);
        Assert.False(store.TryConsume(token, out var second));
        Assert.Null(second);
    }

    [Fact]
    public void ExpiredTicket_IsRejected()
    {
        var time = new MutableTimeProvider();
        var store = new TestStore(TimeSpan.FromSeconds(30), time);
        var token = store.Issue("payload");

        time.Advance(TimeSpan.FromSeconds(30));

        Assert.False(store.TryConsume(token, out var ticket));
        Assert.Null(ticket);
    }

    [Fact]
    public void TicketWithinLifetime_IsAccepted()
    {
        var time = new MutableTimeProvider();
        var store = new TestStore(TimeSpan.FromSeconds(30), time);
        var token = store.Issue("payload");

        time.Advance(TimeSpan.FromSeconds(29));

        Assert.True(store.TryConsume(token, out _));
    }

    [Fact]
    public void Tokens_AreUniqueAndUnguessable()
    {
        var store = new TestStore(TimeSpan.FromMinutes(5), new MutableTimeProvider());
        var a = store.Issue("a");
        var b = store.Issue("b");

        Assert.NotEqual(a, b);
        Assert.Equal(64, a.Length); // 256 bits, hex-encoded
    }

    [Fact]
    public void ConcreteStores_KeepTheirLifetimes()
    {
        Assert.Equal(TimeSpan.FromMinutes(5), new FileDownloadTicketStore().Lifetime);
        Assert.Equal(TimeSpan.FromMinutes(5), new ZipDownloadTicketStore().Lifetime);
        Assert.Equal(TimeSpan.FromMinutes(5), new PublicDownloadTicketStore().Lifetime);
        Assert.Equal(TimeSpan.FromMinutes(5), new CloudAccessDownloadTicketStore().Lifetime);
        Assert.Equal(TimeSpan.FromSeconds(30), new WebSessionTicketStore().Lifetime);
    }
}

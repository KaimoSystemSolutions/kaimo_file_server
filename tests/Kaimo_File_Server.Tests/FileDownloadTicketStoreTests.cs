using Kaimo_File_Server.Web.Services;
using Xunit;

namespace Kaimo_File_Server.Tests;

/// <summary>
/// The download ticket is a single-use capability that grants a file stream, so the
/// security-relevant guarantees are: a fresh ticket resolves once, and a replayed or
/// unknown ticket is rejected.
/// </summary>
public class FileDownloadTicketStoreTests
{
    [Fact]
    public void IssuedTicket_IsConsumableExactlyOnce()
    {
        var store = new FileDownloadTicketStore();
        var ticket = new FileDownloadTicket(Guid.NewGuid(), Guid.NewGuid(), "docs/file.txt", "file.txt");

        var token = store.Issue(ticket);

        Assert.True(store.TryConsume(token, out var first));
        Assert.Equal(ticket, first);

        // Replaying the same token must fail — a download link can't be reused.
        Assert.False(store.TryConsume(token, out var second));
        Assert.Null(second);
    }

    [Fact]
    public void UnknownToken_IsRejected()
    {
        var store = new FileDownloadTicketStore();
        Assert.False(store.TryConsume("not-a-real-token", out var ticket));
        Assert.Null(ticket);
    }
}

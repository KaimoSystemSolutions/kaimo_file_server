using Kaimo_File_Server.Web.Services;
using Xunit;

namespace Kaimo_File_Server.Tests;

public sealed class CloudAuthorizationTicketStoreTests
{
    [Fact]
    public void Ticket_IsBoundToSharePathAndProvider()
    {
        var store = new CloudAuthorizationTicketStore();
        var shareId = Guid.NewGuid();
        var token = store.Issue(shareId, "documents", "google");

        Assert.True(store.IsValid(token, shareId, "documents", "google"));
        Assert.False(store.IsValid(token, Guid.NewGuid(), "documents", "google"));
        Assert.False(store.IsValid(token, shareId, "other", "google"));
        Assert.False(store.IsValid(token, shareId, "documents", "another-provider"));
    }

    [Fact]
    public void Ticket_CanOnlyBeConsumedOnce()
    {
        var store = new CloudAuthorizationTicketStore();
        var shareId = Guid.NewGuid();
        var token = store.Issue(shareId, "", "google");

        Assert.True(store.TryConsume(token, shareId, "", "google"));
        Assert.False(store.TryConsume(token, shareId, "", "google"));
        Assert.False(store.IsValid(token, shareId, "", "google"));
    }
}

using Kaimo_File_Server.Tests.Infrastructure;
using Kaimo_File_Server.Web.Services;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Kaimo_File_Server.Tests;

public sealed class CloudAuthorizationTicketStoreTests : DatabaseTestBase
{
    [Fact]
    public async Task Ticket_IsPersistedAsHashAndBoundToCompleteContext()
    {
        var store = new CloudAuthorizationTicketStore(DbFactory, TimeProvider.System);
        var resourceId = Guid.NewGuid();
        var actorId = Guid.NewGuid();
        var departmentId = Guid.NewGuid();
        var token = await store.IssueAsync(
            resourceId, "documents", "google", actorId, departmentId);

        Assert.True(await store.IsValidAsync(token, resourceId, "documents", "google"));
        Assert.False(await store.IsValidAsync(token, Guid.NewGuid(), "documents", "google"));
        Assert.False(await store.IsValidAsync(token, resourceId, "other", "google"));
        Assert.False(await store.IsValidAsync(token, resourceId, "documents", "another-provider"));
        Assert.False(await store.IsValidAsync(
            token, resourceId, "documents", "google", Guid.NewGuid(), departmentId));
        Assert.False(await store.IsValidAsync(
            token, resourceId, "documents", "google", actorId, Guid.NewGuid()));
        Assert.True(await store.IsValidAsync(
            token, resourceId, "documents", "google", actorId, departmentId));

        await using var db = await DbFactory.CreateDbContextAsync();
        var transaction = await db.StorageAuthorizationTransactions.SingleAsync();
        Assert.NotEqual(token, transaction.TokenHash);
        Assert.DoesNotContain(token, transaction.TokenHash, StringComparison.Ordinal);
        Assert.Equal(actorId, transaction.InitiatingUserId);
        Assert.Equal(departmentId, transaction.DepartmentId);
    }

    [Fact]
    public async Task Ticket_CanOnlyBeConsumedOnceAcrossStoreInstances()
    {
        var issuer = new CloudAuthorizationTicketStore(DbFactory, TimeProvider.System);
        var consumer = new CloudAuthorizationTicketStore(DbFactory, TimeProvider.System);
        var resourceId = Guid.NewGuid();
        var token = await issuer.IssueAsync(resourceId, "", "google");

        Assert.True(await consumer.TryConsumeAsync(token, resourceId, "", "google"));
        Assert.False(await issuer.TryConsumeAsync(token, resourceId, "", "google"));
        Assert.False(await consumer.IsValidAsync(token, resourceId, "", "google"));
    }
}

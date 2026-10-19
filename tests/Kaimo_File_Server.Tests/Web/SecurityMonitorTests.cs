using Kaimo_File_Server.Core.Security;
using Kaimo_File_Server.Infrastructure.Repositories;
using Kaimo_File_Server.Tests.Infrastructure;
using Kaimo_File_Server.Web.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Kaimo_File_Server.Tests;

/// <summary>
/// The security overview end to end on the real EF schema (in-memory SQLite): the monitor
/// buffers attempts and hourly request buckets, the flush persists them (adding to an existing
/// bucket instead of duplicating it), and the repository summarizes them per account and client.
/// </summary>
public sealed class SecurityMonitorTests : DatabaseTestBase
{
    private SecurityEventRepository Repo() => new(DbFactory);

    [Fact]
    public async Task Flush_PersistsAttempts_AndSummarizesPerAccount()
    {
        var monitor = new SecurityMonitor();
        monitor.RecordLogin(SecurityChannel.Web, " Admin ", "10.0.0.1", LoginResult.InvalidCredentials);
        monitor.RecordLogin(SecurityChannel.Api, "admin", "10.0.0.2", LoginResult.LockedOut(TimeSpan.FromMinutes(15)));
        monitor.RecordLogin(SecurityChannel.WebDav, "alice", "10.0.0.3", LoginResult.ForSuccess(null!));

        await monitor.FlushAsync(Repo());
        await monitor.FlushAsync(Repo()); // nothing left: must not duplicate

        var now = DateTime.UtcNow;
        var since = now.AddDays(-1);
        var totals = await Repo().GetTotalsAsync(since, now);
        Assert.Equal(1, totals.Successes);
        Assert.Equal(2, totals.Failures);
        Assert.Equal(1, totals.ActiveLockouts);

        var accounts = await Repo().SummarizeAccountsAsync(since, now, 10);
        var admin = accounts[0]; // locked accounts come first
        Assert.Equal("admin", admin.Username);
        Assert.Equal(1, admin.Failures);
        Assert.Equal(1, admin.Lockouts);
        Assert.Equal(2, admin.Addresses);
        Assert.Equal("10.0.0.2", admin.LastAddress);
        Assert.NotNull(admin.LockedUntilUtc);

        var failed = await Repo().ListLoginAttemptsAsync(since, "ADM", failedOnly: true, 10);
        Assert.Equal(2, failed.Count);
        Assert.Equal(SecurityChannel.Api, failed[0].Channel); // newest first
    }

    [Fact]
    public async Task Flush_AddsToExistingHourlyBucket_AndCountsRejected()
    {
        var monitor = new SecurityMonitor();
        monitor.RecordRequest("1.2.3.4", SecurityChannel.Api, 200, "/api/v1/files");
        monitor.RecordRequest("1.2.3.4", SecurityChannel.Api, 401, "/api/v1/files");
        await monitor.FlushAsync(Repo());
        monitor.RecordRequest("1.2.3.4", SecurityChannel.WebDav, 429, "/dav/share");
        monitor.RecordRequest("5.6.7.8", SecurityChannel.Api, 200, "/api/v1/auth/login");
        await monitor.FlushAsync(Repo());

        await using (var db = NewContext())
            Assert.Equal(2, await db.ClientActivityBuckets.CountAsync());

        var clients = await Repo().SummarizeClientsAsync(DateTime.UtcNow.AddDays(-1), 10);
        var busiest = clients[0];
        Assert.Equal("1.2.3.4", busiest.Address);
        Assert.Equal(2, busiest.ApiRequests);
        Assert.Equal(1, busiest.WebDavRequests);
        Assert.Equal(2, busiest.RejectedRequests);
        Assert.Equal("/dav/share", busiest.LastPath);
    }

    [Fact]
    public async Task ReadOnlyDemo_PersistsNothing()
    {
        var monitor = new SecurityMonitor(new DemoModeOptions { ReadOnly = true });
        monitor.RecordLogin(SecurityChannel.Web, "admin", "10.0.0.1", LoginResult.InvalidCredentials);
        await monitor.FlushAsync(Repo());

        await using var db = NewContext();
        Assert.Equal(0, await db.LoginAttempts.CountAsync());
    }

    [Fact]
    public async Task Prune_RemovesEntriesOlderThanCutoff()
    {
        var monitor = new SecurityMonitor();
        monitor.RecordLogin(SecurityChannel.Web, "admin", "10.0.0.1", LoginResult.InvalidCredentials);
        monitor.RecordRequest("1.2.3.4", SecurityChannel.Api, 200, "/api/v1/files");
        await monitor.FlushAsync(Repo());

        Assert.Equal(0, await Repo().PruneOlderThanAsync(DateTime.UtcNow.AddMinutes(-5)));
        Assert.Equal(2, await Repo().PruneOlderThanAsync(DateTime.UtcNow.AddMinutes(5)));
    }

    [Theory]
    [InlineData("/api/v1/auth/login", SecurityChannel.Api)]
    [InlineData("/dav/share/file.txt", SecurityChannel.WebDav)]
    [InlineData("/_blazor", SecurityChannel.Web)]
    [InlineData("/apiary", SecurityChannel.Web)]
    public void ChannelOf_MapsRequestPath(string path, SecurityChannel expected)
        => Assert.Equal(expected, SecurityMonitorMiddleware.ChannelOf(new PathString(path)));
}

using Bunit;
using Bunit.TestDoubles;
using Kaimo_File_Server.Core.Domain.Identity;
using Kaimo_File_Server.Core.Logging;
using Kaimo_File_Server.Core.Repositories;
using Kaimo_File_Server.Core.Security;
using Kaimo_File_Server.Core.Services;
using Kaimo_File_Server.Web.Components.Pages.Settings.Components;
using Kaimo_File_Server.Web.Components.ViewModels;
using Kaimo_File_Server.Web.Services;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Kaimo_File_Server.Tests;

public class LogViewerComponentTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);

    // Renders the real LogViewer against a mocked reader that honours the level
    // allow-list, then drives the level dropdown exactly as a user would.
    [Fact]
    public void Information_is_off_by_default_and_toggles_the_visible_list()
    {
        using var ctx = new Bunit.TestContext();
        var cut = RenderViewer(ctx, _ =>
        [
            new() { Sequence = 2, Level = LogLevel.Error, Service = "web", Message = "err-msg" },
            new() { Sequence = 1, Level = LogLevel.Information, Service = "web", Message = "info-msg" },
        ]);

        // Default excludes Information: only the error loads, the info row does not.
        cut.WaitForAssertion(() => Assert.Contains("err-msg", cut.Markup), Timeout);
        Assert.DoesNotContain("info-msg", cut.Markup);

        EnableInformation(cut);
        cut.WaitForAssertion(() => Assert.Contains("info-msg", cut.Markup), Timeout);
        Assert.Contains("err-msg", cut.Markup);
    }

    // Regression: Sequence restarts at 1 on every process restart while Instance
    // (the container hostname) stays the same, so Information startup logs from
    // two restarts share Instance:Sequence. A duplicate @key used to throw in the
    // Blazor diff, kill the circuit and freeze the page on the old results.
    [Fact]
    public void Information_from_multiple_restarts_renders_without_duplicate_key_crash()
    {
        var day = new DateTimeOffset(2026, 9, 26, 8, 0, 0, TimeSpan.Zero);
        using var ctx = new Bunit.TestContext();
        var cut = RenderViewer(ctx, _ =>
        [
            new() { TimestampUtc = day.AddHours(2), Sequence = 9, Level = LogLevel.Error, Service = "web", Instance = "3b0f4275a62e", Message = "err-msg" },
            new() { TimestampUtc = day.AddHours(1), Sequence = 5, Level = LogLevel.Information, Service = "web", Instance = "3b0f4275a62e", Message = "second-start" },
            new() { TimestampUtc = day, Sequence = 5, Level = LogLevel.Information, Service = "web", Instance = "3b0f4275a62e", Message = "first-start" },
        ]);
        cut.WaitForAssertion(() => Assert.Contains("err-msg", cut.Markup), Timeout);

        EnableInformation(cut);

        cut.WaitForAssertion(() =>
        {
            Assert.Contains("second-start", cut.Markup);
            Assert.Contains("first-start", cut.Markup);
        }, Timeout);
        Assert.Equal(3, cut.FindAll("details.log-row").Count);
    }

    // Regression: picking another day must replace the table with that day's rows,
    // including a day whose rows span several restarts of the same container.
    [Fact]
    public void Changing_the_date_loads_the_selected_days_entries()
    {
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var pastDay = new DateOnly(2026, 9, 20);
        var pastStart = new DateTimeOffset(2026, 9, 20, 7, 0, 0, TimeSpan.Zero);
        using var ctx = new Bunit.TestContext();
        var cut = RenderViewer(ctx, query => query.UtcDate == pastDay
            ?
            [
                new() { TimestampUtc = pastStart.AddHours(3), Sequence = 5, Level = LogLevel.Warning, Service = "web", Instance = "3b0f4275a62e", Message = "past-restart-2" },
                new() { TimestampUtc = pastStart, Sequence = 5, Level = LogLevel.Warning, Service = "web", Instance = "3b0f4275a62e", Message = "past-restart-1" },
            ]
            : query.UtcDate == today
                ? [new() { TimestampUtc = DateTimeOffset.UtcNow, Sequence = 1, Level = LogLevel.Warning, Service = "web", Instance = "3b0f4275a62e", Message = "today-msg" }]
                : []);
        cut.WaitForAssertion(() => Assert.Contains("today-msg", cut.Markup), Timeout);

        cut.Find("input.log-date-input").Change("2026-09-20");

        cut.WaitForAssertion(() =>
        {
            Assert.Contains("past-restart-2", cut.Markup);
            Assert.Contains("past-restart-1", cut.Markup);
            Assert.DoesNotContain("today-msg", cut.Markup);
        }, Timeout);
    }

    [Fact]
    public void Row_key_distinguishes_restarts_with_the_same_sequence()
    {
        var first = new LogArchiveEntry { Service = "web", Instance = "i", Sequence = 5, TimestampUtc = DateTimeOffset.UnixEpoch };
        var second = new LogArchiveEntry { Service = "web", Instance = "i", Sequence = 5, TimestampUtc = DateTimeOffset.UnixEpoch.AddSeconds(1) };
        Assert.NotEqual(LogViewerViewModel.RowKey(first), LogViewerViewModel.RowKey(second));
    }

    private static void EnableInformation(IRenderedComponent<LogViewer> cut)
    {
        // "Information" is the first option in the level dropdown.
        cut.Find(".multi-select .multi-select-display").Click();
        cut.Find(".multi-select-dropdown .multi-select-option input").Change(true);
    }

    // The mocked reader applies the level allow-list itself; the callback decides
    // which rows exist for a given query (e.g. per UtcDate).
    private static IRenderedComponent<LogViewer> RenderViewer(
        Bunit.TestContext ctx,
        Func<LogArchiveQuery, List<LogArchiveEntry>> entries)
    {
        var reader = new Mock<ILogArchiveReader>();
        reader.Setup(r => r.GetSourcesAsync(It.IsAny<CancellationToken>()))
              .ReturnsAsync(new[] { "web" });
        reader.Setup(r => r.QueryAsync(It.IsAny<LogArchiveQuery>(), It.IsAny<CancellationToken>()))
              .ReturnsAsync((LogArchiveQuery query, CancellationToken _) =>
              {
                  var all = entries(query);
                  var filtered = query.Levels is null
                      ? all
                      : all.Where(e => query.Levels.Contains(e.Level)).ToList();
                  return new LogArchiveQueryResult(filtered, false);
              });

        var authz = ctx.AddTestAuthorization();
        authz.SetAuthorized("tester");

        var userContext = new UserContext(
            new User(Guid.NewGuid(), "Tester", "tester", "hash", "hash"), [], [], []);
        var userContextFactory = new Mock<IUserContextFactory>();
        userContextFactory.Setup(f => f.CreateByUsernameAsync("tester")).ReturnsAsync(userContext);

        var managementAuth = new Mock<IManagementAuthService>();
        managementAuth
            .Setup(a => a.HasGlobalPermissionAsync(It.IsAny<UserContext>(), ManagementPermission.ViewSystemLogs))
            .ReturnsAsync(true);

        var tokens = new LogDownloadTokenService(new EphemeralDataProtectionProvider());

        ctx.Services.AddScoped(sp => new LogViewerViewModel(
            reader.Object,
            tokens,
            sp.GetRequiredService<AuthenticationStateProvider>(),
            userContextFactory.Object,
            managementAuth.Object,
            NullLogger<LogViewerViewModel>.Instance));

        return ctx.RenderComponent<LogViewer>();
    }
}

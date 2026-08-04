using Kaimo_File_Server.Core.Domain;
using Kaimo_File_Server.Core.Domain.Identity;
using Kaimo_File_Server.Core.Repositories;
using Kaimo_File_Server.Core.Services;
using Kaimo_File_Server.Infrastructure.Clouds;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Kaimo_File_Server.Tests;

public sealed class CloudSyncSchedulerServiceTests
{
    [Fact]
    public void Schedule_DefaultsToSixtySecondsAndAcceptsPerEntryOverrides()
    {
        var defaults = new CloudSyncSchedule();
        var overridden = new CloudSyncSchedule
        {
            IntervalSeconds = 15
        };

        Assert.Equal(60, defaults.GetEffectiveIntervalSeconds());
        Assert.True(defaults.HasValidInterval);
        Assert.Equal(15, overridden.GetEffectiveIntervalSeconds());
        Assert.True(overridden.HasValidInterval);
        Assert.False(new CloudSyncSchedule
        {
            IntervalSeconds = 0
        }.HasValidInterval);
    }

    [Fact]
    public void IsDue_RequiresEnabledCurrentSlotAndNoSuccessfulRunInCurrentHour()
    {
        var schedule = new CloudSyncSchedule
        {
            IsEnabled = true,
            ActiveSlots = [CloudSyncSchedule.ToSlot(DayOfWeek.Monday, 10)]
        };
        var now = new DateTimeOffset(2026, 8, 3, 10, 42, 0, TimeSpan.Zero);

        Assert.True(CloudSyncSchedulerService.IsDue(
            schedule, new DateTime(2026, 8, 3, 9, 59, 0, DateTimeKind.Utc), now, TimeZoneInfo.Utc));
        Assert.False(CloudSyncSchedulerService.IsDue(
            schedule, new DateTime(2026, 8, 3, 10, 5, 0, DateTimeKind.Utc), now, TimeZoneInfo.Utc));

        schedule.IsEnabled = false;
        Assert.False(CloudSyncSchedulerService.IsDue(
            schedule, null, now, TimeZoneInfo.Utc));
    }

    [Fact]
    public async Task CheckNowAsync_RunsDueMappingWithPersistedExecutionUser()
    {
        var now = new DateTimeOffset(2026, 8, 3, 10, 15, 0, TimeSpan.Zero);
        var time = new FixedTimeProvider(now);
        var share = new ShareDefinition("docs", "/data/docs");
        share.CloudSettings.Folders["projects"] = new SyncedFolder(
            "google", new Dictionary<string, string>())
        {
            Schedule = new CloudSyncSchedule
            {
                IsEnabled = true,
                RunAsUsername = "scheduler",
                ActiveSlots = [CloudSyncSchedule.ToSlot(DayOfWeek.Monday, 10)]
            }
        };

        var actor = new UserContext(
            new User(Guid.NewGuid(), "Scheduler", "scheduler", "hash", "nt"),
            [], [], []);
        var shares = new Mock<IShareRepository>();
        shares.Setup(repository => repository.GetAllEnabledAsync())
            .ReturnsAsync([share]);
        var users = new Mock<IUserContextFactory>();
        users.Setup(factory => factory.CreateByUsernameAsync("scheduler"))
            .ReturnsAsync(actor);
        var execution = new Mock<ICloudSyncExecutionService>();
        execution.Setup(service => service.RunAsync(
                share.Id,
                "projects",
                actor,
                null,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(CloudSyncExecutionResult.Completed);

        var services = new ServiceCollection();
        services.AddSingleton(shares.Object);
        services.AddSingleton(users.Object);
        services.AddSingleton(execution.Object);
        await using ServiceProvider provider = services.BuildServiceProvider();
        var sut = new CloudSyncSchedulerService(
            provider.GetRequiredService<IServiceScopeFactory>(),
            time,
            new CloudSyncSchedulerSignal(),
            NullLogger<CloudSyncSchedulerService>.Instance);

        await sut.CheckNowAsync();

        execution.Verify(service => service.RunAsync(
            share.Id,
            "projects",
            actor,
            null,
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task CheckNowAsync_RespectsEachMappingsOwnEvaluationInterval()
    {
        var time = new FixedTimeProvider(
            new DateTimeOffset(2026, 8, 3, 10, 15, 0, TimeSpan.Zero));
        var share = new ShareDefinition("docs", "/data/docs");
        share.CloudSettings.Folders["projects"] = new SyncedFolder(
            "google", new Dictionary<string, string>())
        {
            Schedule = new CloudSyncSchedule
            {
                IsEnabled = true,
                IntervalSeconds = 15,
                RunAsUsername = "scheduler",
                ActiveSlots = [CloudSyncSchedule.ToSlot(DayOfWeek.Monday, 10)]
            }
        };
        var actor = new UserContext(
            new User(Guid.NewGuid(), "Scheduler", "scheduler", "hash", "nt"),
            [], [], []);
        var shares = new Mock<IShareRepository>();
        shares.Setup(repository => repository.GetAllEnabledAsync()).ReturnsAsync([share]);
        var users = new Mock<IUserContextFactory>();
        users.Setup(factory => factory.CreateByUsernameAsync("scheduler")).ReturnsAsync(actor);
        var execution = new Mock<ICloudSyncExecutionService>();
        execution.Setup(service => service.RunAsync(
                share.Id, "projects", actor, null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(CloudSyncExecutionResult.Completed);
        var services = new ServiceCollection();
        services.AddSingleton(shares.Object);
        services.AddSingleton(users.Object);
        services.AddSingleton(execution.Object);
        await using ServiceProvider provider = services.BuildServiceProvider();
        var sut = new CloudSyncSchedulerService(
            provider.GetRequiredService<IServiceScopeFactory>(),
            time,
            new CloudSyncSchedulerSignal(),
            NullLogger<CloudSyncSchedulerService>.Instance);

        Assert.Equal(TimeSpan.FromSeconds(15), await sut.CheckNowAsync());
        time.Advance(TimeSpan.FromSeconds(14));
        Assert.Equal(TimeSpan.FromSeconds(1), await sut.CheckNowAsync());
        execution.Verify(service => service.RunAsync(
            share.Id, "projects", actor, null, It.IsAny<CancellationToken>()), Times.Once);

        time.Advance(TimeSpan.FromSeconds(1));
        await sut.CheckNowAsync();
        execution.Verify(service => service.RunAsync(
            share.Id, "projects", actor, null, It.IsAny<CancellationToken>()), Times.Exactly(2));
    }

    [Fact]
    public void CloudSettings_RoundTripsHourlySchedule()
    {
        var settings = new CloudSettings(new Dictionary<string, SyncedFolder>
        {
            ["projects"] = new SyncedFolder("google", new Dictionary<string, string>())
            {
                Schedule = new CloudSyncSchedule
                {
                    IsEnabled = true,
                    IntervalSeconds = 15,
                    RunAsUsername = "scheduler",
                    ActiveSlots =
                    [
                        CloudSyncSchedule.ToSlot(DayOfWeek.Monday, 8),
                        CloudSyncSchedule.ToSlot(DayOfWeek.Friday, 18)
                    ]
                }
            }
        });

        CloudSettings restored = CloudSettings.Deserialize(settings.Serialize());
        CloudSyncSchedule schedule = restored.Folders["projects"].Schedule;

        Assert.True(schedule.IsEnabled);
        Assert.Equal("scheduler", schedule.RunAsUsername);
        Assert.Equal(15, schedule.IntervalSeconds);
        Assert.True(schedule.IsActive(DayOfWeek.Monday, 8));
        Assert.True(schedule.IsActive(DayOfWeek.Friday, 18));
        Assert.False(schedule.IsActive(DayOfWeek.Sunday, 8));
    }

    [Fact]
    public void CloudSettings_LegacyJsonDefaultsToDisabledSchedule()
    {
        const string legacyJson =
            """{"Folders":{"projects":{"Provider":"google","Data":{},"RemotePath":"/","LastSync":null,"Mode":0}}}""";

        CloudSyncSchedule schedule = CloudSettings.Deserialize(legacyJson)
            .Folders["projects"].Schedule;

        Assert.NotNull(schedule);
        Assert.False(schedule.IsEnabled);
        Assert.Empty(schedule.ActiveSlots);
        Assert.Equal(CloudSyncSchedule.DefaultIntervalSeconds, schedule.IntervalSeconds);
    }

    private sealed class FixedTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        private DateTimeOffset _utcNow = utcNow;

        public override DateTimeOffset GetUtcNow() => _utcNow;
        public override TimeZoneInfo LocalTimeZone => TimeZoneInfo.Utc;

        public void Advance(TimeSpan amount) => _utcNow += amount;
    }
}

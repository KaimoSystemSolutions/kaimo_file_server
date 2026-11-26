using Kaimo_File_Server.Core.Domain;
using Kaimo_File_Server.Core.Domain.Identity;
using Kaimo_File_Server.Core.Language;
using Kaimo_File_Server.Core.Repositories.ExternalStorage;
using Kaimo_File_Server.Core.Repositories.Identity;
using Kaimo_File_Server.Core.Repositories.Shares;
using Kaimo_File_Server.Core.Services.CloudSync;
using Kaimo_File_Server.Infrastructure.ExternalStorage.CloudSync;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Kaimo_File_Server.Infrastructure.Tests.ExternalStorage.CloudSync;

/// <summary>
/// A scheduled sync of a folder on the home-folder share shows up in the Running Jobs menu with
/// the home (named by its owner's GUID) as the owner's username, like the file browser. The job
/// itself keeps the stored path; folders of other shares are never relabelled.
/// </summary>
public sealed class CloudSyncSchedulerHomeLabelTests
{
    private readonly UserContext _actor = new(new User(Guid.NewGuid(), "Scheduler", "scheduler", "hash", "nt"), [], [], []);
    private readonly User _bob = new(Guid.NewGuid(), "Bob", "bob", "hash", "nt");
    private readonly ShareDefinition _homes = new("users", "/data/users", isEnabled: true) { IsUserHomes = true };
    private readonly ShareDefinition _projects = new("projects", "/data/projects", isEnabled: true);
    private readonly Mock<ICloudSyncJobRunner> _jobRunner = new();

    private string BobHome => _bob.Id.ToString("D");

    private async Task RunDueAsync(ShareDefinition share, string localPath, string? displayName)
    {
        var definition = new SyncDefinition
        {
            LocalShareId = share.Id,
            LocalPath = localPath,
            RemotePath = "/remote",
            Mode = SyncMode.Push,
            DisplayName = displayName,
            RunAsUserId = _actor.User.Id,
            Schedule = new CloudSyncSchedule
            {
                IsEnabled = true,
                ActiveSlots = [CloudSyncSchedule.ToSlot(DayOfWeek.Monday, 10)]
            }
        };
        var definitions = new Mock<ISyncDefinitionRepository>();
        definitions.Setup(r => r.GetEnabledScheduledAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync([new SyncDefinitionScheduleEntry(definition, null)]);
        var contexts = new Mock<IUserContextFactory>();
        contexts.Setup(f => f.CreateByUserIdAsync(_actor.User.Id)).ReturnsAsync(_actor);
        var shares = new Mock<IShareRepository>();
        shares.Setup(s => s.GetByIdAsync(_homes.Id)).ReturnsAsync(_homes);
        shares.Setup(s => s.GetByIdAsync(_projects.Id)).ReturnsAsync(_projects);
        var users = new Mock<IUserRepository>();
        users.Setup(u => u.GetByIdAsync(_bob.Id)).ReturnsAsync(_bob);

        var services = new ServiceCollection();
        services.AddSingleton(definitions.Object);
        services.AddSingleton(Mock.Of<ILegacyCloudSyncMigrationService>());
        services.AddSingleton(contexts.Object);
        services.AddSingleton(shares.Object);
        services.AddSingleton(users.Object);
        await using ServiceProvider provider = services.BuildServiceProvider();
        var sut = new CloudSyncSchedulerService(
            provider.GetRequiredService<IServiceScopeFactory>(),
            new MondayMorning(),
            new CloudSyncSchedulerSignal(),
            _jobRunner.Object,
            NullLogger<CloudSyncSchedulerService>.Instance);

        await sut.CheckNowAsync();
    }

    // The job title as the scheduler formats it in the current UI culture.
    private static string Title(string name)
        => string.Format(Resources.ResourceManager.GetString("Web_Jobs_CloudSync_Title") ?? "Sync: {0}", name);

    private void VerifyEnqueued(ShareDefinition share, string localPath, string title, string detail)
        => _jobRunner.Verify(r => r.Enqueue(share.Id, localPath, _actor.User.Id, title, detail), Times.Once);

    [Fact]
    public async Task A_home_folder_is_described_by_its_owners_username_while_the_job_keeps_the_stored_path()
    {
        await RunDueAsync(_homes, $"{BobHome}/docs", "Bob's docs");

        VerifyEnqueued(_homes, $"{BobHome}/docs", Title("Bob's docs"), "bob/docs → /remote");
    }

    [Fact]
    public async Task An_unnamed_sync_of_a_home_folder_is_titled_with_the_owners_username()
    {
        await RunDueAsync(_homes, BobHome, displayName: null);

        VerifyEnqueued(_homes, BobHome, Title("bob"), "bob → /remote");
    }

    [Fact]
    public async Task A_home_folder_whose_owner_no_longer_exists_keeps_its_folder_name()
    {
        var orphan = Guid.NewGuid().ToString("D");

        await RunDueAsync(_homes, $"{orphan}/docs", displayName: null);

        VerifyEnqueued(_homes, $"{orphan}/docs", Title($"{orphan}/docs"), $"{orphan}/docs → /remote");
    }

    [Fact]
    public async Task A_folder_of_another_share_is_never_relabelled()
    {
        await RunDueAsync(_projects, $"{BobHome}/docs", displayName: null);

        VerifyEnqueued(_projects, $"{BobHome}/docs", Title($"{BobHome}/docs"), $"{BobHome}/docs → /remote");
    }

    // Monday 10:15 UTC: inside the schedule's only active slot.
    private sealed class MondayMorning : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => new(2026, 8, 3, 10, 15, 0, TimeSpan.Zero);
        public override TimeZoneInfo LocalTimeZone => TimeZoneInfo.Utc;
    }
}

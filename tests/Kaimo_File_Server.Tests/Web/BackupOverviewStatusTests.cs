using Kaimo_File_Server.Core.Domain.Backup;
using Kaimo_File_Server.Infrastructure.Backup.Restic;
using Kaimo_File_Server.Web.Components.Shared;
using Kaimo_File_Server.Web.Components.ViewModels.Backup;
using Xunit;

namespace Kaimo_File_Server.Tests;

/// <summary>
/// The backup overview turns job and repository state into short status messages: one per
/// object that needs attention, problems before warnings, plus a hint when nothing is set up.
/// </summary>
public class BackupOverviewStatusTests
{
    private static readonly DateTime Now = new(2026, 10, 28, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void HealthySetup_NeedsNoAttention()
    {
        var items = BackupOverviewStatus.Attention([Job("Daily", BackupRunStatus.Succeeded)], [Repo("NAS")], canManageJobs: true, Now);

        Assert.Empty(items);
    }

    [Fact]
    public void FailedJob_IsAProblem()
    {
        var job = Job("Daily", BackupRunStatus.Failed);

        var item = Assert.Single(BackupOverviewStatus.Attention([job], [Repo("NAS")], true, Now));

        Assert.Equal(BackupAttentionReason.JobFailed, item.Reason);
        Assert.Equal(StatusTone.Negative, item.Tone);
        Assert.Equal(job.Job.Id, item.Id);
    }

    [Fact]
    public void OverdueJob_IsAWarning()
    {
        var job = Job("Weekly", BackupRunStatus.Succeeded, lastSuccess: Now.AddDays(-10));

        var item = Assert.Single(BackupOverviewStatus.Attention([job], [Repo("NAS")], true, Now));

        Assert.Equal(BackupAttentionReason.JobOverdue, item.Reason);
        Assert.Equal(StatusTone.Warning, item.Tone);
    }

    [Fact]
    public void JobWithWarnings_IsAWarning()
    {
        var item = Assert.Single(BackupOverviewStatus.Attention([Job("Daily", BackupRunStatus.SucceededWithWarnings)], [Repo("NAS")], true, Now));

        Assert.Equal(BackupAttentionReason.JobWarnings, item.Reason);
    }

    [Fact]
    public void FailedAndOverdueJob_GetsOneMessage()
    {
        var job = Job("Daily", BackupRunStatus.Failed, lastSuccess: Now.AddDays(-10));

        Assert.Equal(BackupAttentionReason.JobFailed, Assert.Single(BackupOverviewStatus.Attention([job], [Repo("NAS")], true, Now)).Reason);
    }

    [Fact]
    public void DisabledJobAndRepository_AreNotReported()
    {
        var job = Job("Old", BackupRunStatus.Failed);
        job.Job.Enabled = false;
        var repo = Repo("Old NAS", error: "unreachable");
        repo.Repository.State = BackupRepositoryState.Disabled;

        Assert.Empty(BackupOverviewStatus.Attention([job], [repo, Repo("NAS")], true, Now));
    }

    [Theory]
    [InlineData(BackupRepositoryState.PendingRecoveryKit, null, null, BackupAttentionReason.RepositoryKitPending, StatusTone.Warning)]
    [InlineData(BackupRepositoryState.Active, "unreachable", null, BackupAttentionReason.RepositoryError, StatusTone.Negative)]
    [InlineData(BackupRepositoryState.Active, null, false, BackupAttentionReason.RepositoryCheckFailed, StatusTone.Negative)]
    public void RepositoryProblems_AreReported(BackupRepositoryState state, string? error, bool? checkOk, BackupAttentionReason reason, StatusTone tone)
    {
        var repo = Repo("NAS", error, checkOk);
        repo.Repository.State = state;

        var item = Assert.Single(BackupOverviewStatus.Attention([Job("Daily", BackupRunStatus.Succeeded)], [repo], true, Now));

        Assert.Equal(reason, item.Reason);
        Assert.Equal(tone, item.Tone);
        Assert.Equal(repo.Repository.Id, item.Id);
    }

    [Fact]
    public void WithoutRepositoryAccess_RepositoriesAreNotChecked()
        => Assert.Empty(BackupOverviewStatus.Attention([Job("Daily", BackupRunStatus.Succeeded)], null, true, Now));

    [Fact]
    public void NothingSetUp_HintsAtRepositoriesAndJobs()
    {
        var reasons = BackupOverviewStatus.Attention([], [], true, Now).Select(i => i.Reason);

        Assert.Equal([BackupAttentionReason.NoRepositories, BackupAttentionReason.NoJobs], reasons);
    }

    [Fact]
    public void NoJobs_IsOnlyHintedToJobManagers()
        => Assert.Empty(BackupOverviewStatus.Attention([], null, canManageJobs: false, Now));

    [Fact]
    public void Problems_ComeBeforeWarnings()
    {
        var items = BackupOverviewStatus.Attention(
            [Job("A warning", BackupRunStatus.SucceededWithWarnings), Job("B failed", BackupRunStatus.Failed)], [Repo("NAS")], true, Now);

        Assert.Equal(["B failed", "A warning"], items.Select(i => i.Name));
    }

    [Fact]
    public void JobHealth_CountsLikeTheHomePage()
    {
        var disabled = Job("Off", BackupRunStatus.Succeeded);
        disabled.Job.Enabled = false;

        var health = BackupOverviewStatus.JobHealth(
            [Job("Ok", BackupRunStatus.Succeeded), Job("Warn", BackupRunStatus.SucceededWithWarnings),
             Job("Failed", BackupRunStatus.Failed), Job("New", null), disabled], Now);

        Assert.Equal((1, 1, 1, 2), (health.Ok, health.Warning, health.Failed, health.Inactive));
    }

    [Fact]
    public void RepositoryHealth_CountsStates()
    {
        var kit = Repo("Kit");
        kit.Repository.State = BackupRepositoryState.PendingRecoveryKit;
        var off = Repo("Off");
        off.Repository.State = BackupRepositoryState.Disabled;

        var health = BackupOverviewStatus.RepositoryHealth([Repo("Ok"), kit, Repo("Broken", "unreachable"), off]);

        Assert.Equal((1, 1, 1, 1), (health.Ok, health.Warning, health.Failed, health.Inactive));
    }

    private static BackupJobView Job(string name, BackupRunStatus? status, DateTime? lastSuccess = null)
        => new(new BackupJob
        {
            Name = name,
            Enabled = true,
            LastStatus = status,
            LastRunAtUtc = status is null ? null : Now.AddHours(-1),
            LastSuccessAtUtc = lastSuccess ?? (status is BackupRunStatus.Succeeded or BackupRunStatus.SucceededWithWarnings ? Now.AddHours(-1) : null),
            UpdatedAtUtc = Now.AddDays(-30),
            AlertAfterDays = 3,
            Schedule = new BackupSchedule { Times = [new TimeOnly(2, 0)], Days = [.. Enum.GetValues<DayOfWeek>()] },
        }, "NAS", []);

    private static BackupRepositoryView Repo(string name, string? error = null, bool? checkOk = true)
        => new(new BackupRepository { Name = name, State = BackupRepositoryState.Active, LastErrorCode = error, LastCheckOk = checkOk }, [], 0);
}

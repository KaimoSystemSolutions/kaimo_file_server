using Kaimo_File_Server.Core.Domain.Identity;
using Kaimo_File_Server.Core.Repositories;
using Kaimo_File_Server.Core.Services;
using Kaimo_File_Server.Infrastructure.Clouds;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Kaimo_File_Server.Tests;

public sealed class CloudSyncJobRunnerTests
{
    private static readonly Guid ShareId = Guid.NewGuid();
    private static readonly Guid UserId = Guid.NewGuid();

    [Fact]
    public async Task Enqueue_TracksJob_Dedupes_AndCompletionRemovesIt()
    {
        var (runner, execution, provider) = Build();
        var started = Tcs();
        var release = Tcs();
        SetupGatedRun(execution, "projects", started, release);
        int changes = 0;
        runner.OnChanged += () => Interlocked.Increment(ref changes);
        await runner.StartAsync(CancellationToken.None);

        Guid id = runner.Enqueue(ShareId, "projects", UserId, "Cloud sync", "detail");
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Single(runner.Jobs);
        Assert.True(changes > 0);

        // Same share + path (including a normalization variant) reuses the job.
        Assert.Equal(id, runner.Enqueue(ShareId, "projects", UserId, "x", "y"));
        Assert.Equal(id, runner.Enqueue(ShareId, "projects/", UserId, "x", "y"));
        Assert.Single(runner.Jobs);

        // A different path is a distinct job.
        Guid other = runner.Enqueue(ShareId, "archive", UserId, "x", "y");
        Assert.NotEqual(id, other);
        Assert.Equal(2, runner.Jobs.Count);

        release.SetResult();
        await WaitAsync(() => runner.Jobs.Count == 0);

        await StopAsync(runner, provider);
    }

    [Fact]
    public async Task Run_PassesActorAndCancellableToken_AndReportsProgress()
    {
        var (runner, execution, provider) = Build();
        var started = Tcs();
        var release = Tcs();
        UserContext? seenActor = null;
        bool tokenCancellable = false;
        execution
            .Setup(service => service.RunAsync(
                ShareId, "projects", It.IsAny<UserContext>(),
                It.IsAny<Action<string?, int>?>(), It.IsAny<CancellationToken>()))
            .Returns(async (Guid _, string _, UserContext actor,
                Action<string?, int>? report, CancellationToken token) =>
            {
                seenActor = actor;
                tokenCancellable = token.CanBeCanceled;
                report?.Invoke("Uploading report.pdf", 55);
                started.SetResult();
                await release.Task;
                return CloudSyncExecutionResult.Completed;
            });
        await runner.StartAsync(CancellationToken.None);

        runner.Enqueue(ShareId, "projects", UserId, "Cloud sync", "detail");
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));

        SyncJobSnapshot snapshot = Assert.Single(runner.Jobs);
        Assert.Equal("Uploading report.pdf", snapshot.Detail);
        Assert.Equal(55, snapshot.Progress);
        Assert.Equal(UserId, seenActor!.User.Id);
        Assert.True(tokenCancellable);

        release.SetResult();
        await WaitAsync(() => runner.Jobs.Count == 0);
        await StopAsync(runner, provider);
    }

    [Fact]
    public async Task Cancel_CancelsRunningJob_AndReturnsFalseForUnknownOrRepeated()
    {
        var (runner, execution, provider) = Build();
        var started = Tcs();
        bool tokenObservedCancelled = false;
        execution
            .Setup(service => service.RunAsync(
                ShareId, "projects", It.IsAny<UserContext>(),
                It.IsAny<Action<string?, int>?>(), It.IsAny<CancellationToken>()))
            .Returns(async (Guid _, string _, UserContext _,
                Action<string?, int>? _, CancellationToken token) =>
            {
                started.SetResult();
                try { await Task.Delay(Timeout.Infinite, token); }
                catch (OperationCanceledException) { tokenObservedCancelled = true; throw; }
                return CloudSyncExecutionResult.Completed;
            });
        await runner.StartAsync(CancellationToken.None);

        Guid id = runner.Enqueue(ShareId, "projects", UserId, "Cloud sync", "detail");
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.False(runner.Cancel(Guid.NewGuid()));   // unknown id
        Assert.True(runner.Cancel(id));                 // first request wins
        Assert.False(runner.Cancel(id));                // already cancelling

        await WaitAsync(() => runner.Jobs.Count == 0);
        Assert.True(tokenObservedCancelled);
        await StopAsync(runner, provider);
    }

    [Fact]
    public async Task FailingJob_IsRemoved_AndDoesNotStopTheLoop()
    {
        var (runner, execution, provider) = Build();
        execution
            .Setup(service => service.RunAsync(
                ShareId, "boom", It.IsAny<UserContext>(),
                It.IsAny<Action<string?, int>?>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("provider exploded"));
        await runner.StartAsync(CancellationToken.None);

        runner.Enqueue(ShareId, "boom", UserId, "Cloud sync", "detail");
        runner.Enqueue(ShareId, "healthy", UserId, "Cloud sync", "detail");

        await WaitAsync(() => runner.Jobs.Count == 0);
        // The runner survived the exception and still processed the next job.
        execution.Verify(service => service.RunAsync(
            ShareId, "healthy", It.IsAny<UserContext>(),
            It.IsAny<Action<string?, int>?>(), It.IsAny<CancellationToken>()), Times.Once);
        await StopAsync(runner, provider);
    }

    [Fact]
    public async Task DisabledUser_SkipsExecution_AndClearsTheJob()
    {
        var (runner, execution, provider) = Build(userEnabled: false);
        await runner.StartAsync(CancellationToken.None);

        runner.Enqueue(ShareId, "projects", UserId, "Cloud sync", "detail");

        await WaitAsync(() => runner.Jobs.Count == 0);
        execution.Verify(service => service.RunAsync(
            It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<UserContext>(),
            It.IsAny<Action<string?, int>?>(), It.IsAny<CancellationToken>()), Times.Never);
        await StopAsync(runner, provider);
    }

    // ── helpers ──────────────────────────────────────────────────────────

    private static (CloudSyncJobRunner runner, Mock<ICloudSyncExecutionService> execution, ServiceProvider provider)
        Build(bool userEnabled = true)
    {
        var actor = new UserContext(
            new User(UserId, "Sync", "sync", "hash", "nt", isEnabled: userEnabled), [], [], []);
        var users = new Mock<IUserContextFactory>();
        users.Setup(factory => factory.CreateByUserIdAsync(UserId)).ReturnsAsync(actor);

        var execution = new Mock<ICloudSyncExecutionService>();
        // Default: any run completes immediately unless a test overrides it.
        execution
            .Setup(service => service.RunAsync(
                It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<UserContext>(),
                It.IsAny<Action<string?, int>?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(CloudSyncExecutionResult.Completed);

        var services = new ServiceCollection();
        services.AddScoped(_ => users.Object);
        services.AddScoped(_ => execution.Object);
        ServiceProvider provider = services.BuildServiceProvider();

        var runner = new CloudSyncJobRunner(
            provider.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<CloudSyncJobRunner>.Instance);
        return (runner, execution, provider);
    }

    private static void SetupGatedRun(
        Mock<ICloudSyncExecutionService> execution,
        string localPath,
        TaskCompletionSource started,
        TaskCompletionSource release)
        => execution
            .Setup(service => service.RunAsync(
                ShareId, localPath, It.IsAny<UserContext>(),
                It.IsAny<Action<string?, int>?>(), It.IsAny<CancellationToken>()))
            .Returns(async (Guid _, string _, UserContext _,
                Action<string?, int>? _, CancellationToken _) =>
            {
                started.SetResult();
                await release.Task;
                return CloudSyncExecutionResult.Completed;
            });

    private static TaskCompletionSource Tcs()
        => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private static async Task WaitAsync(Func<bool> condition)
    {
        DateTime deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
                throw new TimeoutException("Condition was not met in time.");
            await Task.Delay(10);
        }
    }

    private static async Task StopAsync(CloudSyncJobRunner runner, ServiceProvider provider)
    {
        await runner.StopAsync(CancellationToken.None);
        runner.Dispose();
        await provider.DisposeAsync();
    }
}

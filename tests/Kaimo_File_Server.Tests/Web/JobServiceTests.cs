using Kaimo_File_Server.Web.Services;
using Xunit;

namespace Kaimo_File_Server.Tests;

public sealed class JobServiceTests
{
    [Fact]
    public void JobLifecycle_ReportsProgressCancellationAndCompletion()
    {
        using var service = new JobService();
        var changes = 0;
        service.OnChanged += () => changes++;
        using var job = service.Start("Cloud sync", "Scanning…", "cloud-sync");

        job.Update("Pushing report.pdf…", 42);
        Assert.True(service.Cancel(job.Id));

        var snapshot = Assert.Single(service.Jobs);
        Assert.Equal("Pushing report.pdf…", snapshot.Detail);
        Assert.Equal(42, snapshot.Progress);
        Assert.True(snapshot.IsCancellationRequested);
        Assert.True(job.CancellationToken.IsCancellationRequested);
        Assert.False(service.Cancel(job.Id));

        job.Complete();
        Assert.Empty(service.Jobs);
        Assert.Equal(4, changes);
    }

    [Fact]
    public void JobProgress_IsClampedToValidPercentage()
    {
        using var service = new JobService();
        using var job = service.Start("Upload", "Starting…", "upload");

        job.Update(progress: 150);

        Assert.Equal(100, Assert.Single(service.Jobs).Progress);
    }
}

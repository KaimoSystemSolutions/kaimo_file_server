using System.Diagnostics;
using Kaimo_File_Server.Core.Domain;
using Kaimo_File_Server.Core.Services.ExternalStorage;
using Kaimo_File_Server.Web.Components.ViewModels;
using Moq;
using Xunit;

namespace Kaimo_File_Server.Web.Tests.ViewModels.CloudAccess;

/// <summary>
/// The manual connection test is bounded by a fixed time limit so the UI can show
/// how long the test may still take, and a hanging remote never blocks the page.
/// </summary>
public sealed class CloudAccessViewModelTestConnectionTests
{
    [Fact]
    public void ConnectionTestTimeout_DefaultsToSixtySeconds()
    {
        var (viewModel, _, _, _) = Arrange();

        Assert.Equal(TimeSpan.FromSeconds(60), CloudAccessViewModel.DefaultConnectionTestTimeout);
        Assert.Equal(CloudAccessViewModel.DefaultConnectionTestTimeout, viewModel.ConnectionTestTimeout);
    }

    [Fact]
    public async Task TestConnectionAsync_HealthyProvider_MarksReady()
    {
        var (viewModel, connection, provider, saved) = Arrange();
        provider.Setup(x => x.TestAsync(It.IsAny<StorageConnection>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new StorageConnectionHealthResult(
                StorageConnectionHealthState.Healthy, "ok", DateTime.UtcNow));

        await viewModel.TestConnectionAsync(connection.Id);

        Assert.Equal(StorageConnectionState.Ready, connection.State);
        Assert.Null(connection.LastErrorCode);
        Assert.Single(saved);
    }

    [Fact]
    public async Task TestConnectionAsync_PassesCancellableTokenToProvider()
    {
        var (viewModel, connection, provider, _) = Arrange();
        CancellationToken received = default;
        provider.Setup(x => x.TestAsync(It.IsAny<StorageConnection>(), It.IsAny<CancellationToken>()))
            .Callback<StorageConnection, CancellationToken>((_, token) => received = token)
            .ReturnsAsync(new StorageConnectionHealthResult(
                StorageConnectionHealthState.Healthy, "ok", DateTime.UtcNow));

        await viewModel.TestConnectionAsync(connection.Id);

        Assert.True(received.CanBeCanceled);
    }

    [Fact]
    public async Task TestConnectionAsync_ProviderHonoringToken_RecordsTimeoutAfterLimit()
    {
        var (viewModel, connection, provider, saved) = Arrange();
        viewModel.ConnectionTestTimeout = TimeSpan.FromMilliseconds(150);
        provider.Setup(x => x.TestAsync(It.IsAny<StorageConnection>(), It.IsAny<CancellationToken>()))
            .Returns<StorageConnection, CancellationToken>(async (_, token) =>
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
                throw new UnreachableException();
            });

        await viewModel.TestConnectionAsync(connection.Id);

        Assert.Equal(CloudAccessViewModel.ConnectionTestTimeoutCode, connection.LastErrorCode);
        Assert.Equal(StorageConnectionState.Degraded, connection.State);
        Assert.NotNull(connection.LastVerifiedAtUtc);
        Assert.Single(saved);
    }

    [Fact]
    public async Task TestConnectionAsync_ProviderIgnoringToken_StillReturnsAfterLimit()
    {
        var (viewModel, connection, provider, saved) = Arrange();
        viewModel.ConnectionTestTimeout = TimeSpan.FromMilliseconds(150);
        var never = new TaskCompletionSource<StorageConnectionHealthResult>();
        provider.Setup(x => x.TestAsync(It.IsAny<StorageConnection>(), It.IsAny<CancellationToken>()))
            .Returns(never.Task);

        var stopwatch = Stopwatch.StartNew();
        await viewModel.TestConnectionAsync(connection.Id);

        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(10));
        Assert.Equal(CloudAccessViewModel.ConnectionTestTimeoutCode, connection.LastErrorCode);
        Assert.Single(saved);
    }

    [Fact]
    public async Task TestConnectionAsync_CallerCancels_ThrowsWithoutRecordingTimeout()
    {
        var (viewModel, connection, provider, saved) = Arrange();
        provider.Setup(x => x.TestAsync(It.IsAny<StorageConnection>(), It.IsAny<CancellationToken>()))
            .Returns<StorageConnection, CancellationToken>(async (_, token) =>
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
                throw new UnreachableException();
            });
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => viewModel.TestConnectionAsync(connection.Id, cancellation.Token));

        Assert.Null(connection.LastErrorCode);
        Assert.Empty(saved);
    }

    private static (CloudAccessViewModel ViewModel, StorageConnection Connection,
        Mock<IStorageConnectionProvider> Provider, List<StorageConnection> Saved) Arrange()
    {
        var connection = CloudAccessViewModelTestFactory.SmbConnection();
        var (viewModel, provider, saved) = CloudAccessViewModelTestFactory.Create(connection);
        return (viewModel, connection, provider, saved);
    }
}

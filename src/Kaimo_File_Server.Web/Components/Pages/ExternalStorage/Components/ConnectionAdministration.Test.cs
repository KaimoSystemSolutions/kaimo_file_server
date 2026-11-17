using System.Diagnostics;
using Kaimo_File_Server.Core.Domain;
using Kaimo_File_Server.Web.Services.Ui;

namespace Kaimo_File_Server.Web.Components.Pages.ExternalStorage.Components;

// Manual connection test with a time-limit progress bar. The test itself is one opaque
// remote round trip, so the bar shows the used share of the view model's time limit.
public partial class ConnectionAdministration
{
    private static readonly TimeSpan TestProgressTick = TimeSpan.FromMilliseconds(250);

    private CancellationTokenSource? _testRun;
    private long? _testStartedAt;
    private Guid? _testingId;

    private bool IsTesting => _testStartedAt is not null;
    // The operator may select another connection while a test runs; the bar belongs to the tested one.
    private bool ShowTestProgress => IsTesting && _selected?.Id == _testingId;
    private TimeSpan TestElapsed => _testStartedAt is long started ? Stopwatch.GetElapsedTime(started) : TimeSpan.Zero;
    private int TestPercent => ConnectionTestProgress.Percent(TestElapsed, VM.ConnectionTestTimeout);
    private string TestProgressText => string.Format(
        R("Web_ExternalStorage_TestRunning"),
        ConnectionTestProgress.RemainingSeconds(TestElapsed, VM.ConnectionTestTimeout));

    private async Task TestAsync()
    {
        _testFailed = false;
        Guid selectedId = _selected!.Id;
        _busy = true; _error = null;
        CancellationToken cancellationToken = StartTestProgress(selectedId);
        try
        {
            await VM.TestConnectionAsync(selectedId, cancellationToken);
            // Reloading replaced the connection instances; keep whatever the operator selected meanwhile.
            Guid? currentId = _selected?.Id;
            _selected = VM.Connections.FirstOrDefault(connection => connection.Id == currentId);
            StorageConnection? tested = VM.Connections.FirstOrDefault(connection => connection.Id == selectedId);
            bool failed = !(tested?.State == StorageConnectionState.Ready
                            && string.IsNullOrWhiteSpace(tested.LastErrorCode));
            // Success is transient (toast); a failure stays as a banner until the next test, but only
            // on the tested connection. Elsewhere its state badge in the list shows the outcome.
            _testFailed = failed && currentId == selectedId;
            if (!failed)
                Toasts.Show(R("Web_ExternalStorage_TestSuccessful"), ToastType.Success);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // The component was disposed mid-test; nothing is left to show.
        }
        catch (Exception exception)
        {
            _error = exception is InvalidOperationException or ArgumentException
                ? exception.Message
                : R("Web_ExternalStorage_OperationFailed");
            _testFailed = _selected?.Id == selectedId;
        }
        finally
        {
            StopTestProgress();
            _busy = false;
        }
    }

    private CancellationToken StartTestProgress(Guid connectionId)
    {
        StopTestProgress();
        _testingId = connectionId;
        _testRun = new CancellationTokenSource();
        _testStartedAt = Stopwatch.GetTimestamp();
        _ = TickTestProgressAsync(_testRun.Token);
        return _testRun.Token;
    }

    // Re-renders a few times per second so the bar and the remaining seconds advance.
    private async Task TickTestProgressAsync(CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(TestProgressTick);
        try
        {
            while (await timer.WaitForNextTickAsync(cancellationToken))
                await InvokeAsync(StateHasChanged);
        }
        catch (OperationCanceledException) { }
        catch (ObjectDisposedException) { }
    }

    // Ends the ticker. While a test is still running (component disposal) it also aborts the
    // test; after a finished test the token is no longer observed, so cancelling is harmless.
    private void StopTestProgress()
    {
        CancellationTokenSource? run = _testRun;
        _testRun = null;
        _testStartedAt = null;
        _testingId = null;
        if (run is null)
            return;
        run.Cancel();
        run.Dispose();
    }
}

using System.Globalization;
using Kaimo_File_Server.Core.Domain;
using Kaimo_File_Server.Core.Services.DataServices;
using Xunit;

namespace Kaimo_File_Server.Tests;

/// <summary>
/// Progress reporting: the pre-scan and the comparison of unchanged files must keep
/// the user informed instead of sitting at 0% on large trees.
/// </summary>
public sealed partial class CloudSyncReconciliationTests
{
    [Fact]
    public async Task Sync_ReportsProgress_EndingAtOneHundredPercent()
    {
        var remote = new InMemoryRemote();
        var local = new InMemoryFileService();
        local.PutFile("a.txt", "content", Time(10));

        var reports = await SyncWithReportsAsync(remote, local, SyncMode.Push);

        Assert.Equal(0, reports[0].Percent);
        Assert.Equal(100, reports[^1].Percent);
        Assert.Contains(reports, r => r.Text!.StartsWith("Uploading a.txt"));
    }

    [Fact]
    public async Task Pull_ReportsRemoteScanWithRunningFileCount_BeforeTransferring()
    {
        var remote = new InMemoryRemote();
        remote.PutFile("/a.txt", "aaaa", Time(10));
        remote.PutDir("/sub");
        remote.PutFile("/sub/b.txt", "bbbb", Time(10));
        var local = new InMemoryFileService();

        var reports = await SyncWithReportsAsync(remote, local, SyncMode.Pull);

        int scan = reports.FindIndex(r => r.Text!.Contains("Scanning remote") && r.Text.Contains("Files found: 2"));
        int firstDownload = reports.FindIndex(r => r.Text!.StartsWith("Downloading"));
        Assert.InRange(scan, 0, firstDownload - 1);
        Assert.Equal(0, reports[scan].Percent);
    }

    [Fact]
    public async Task Push_ReportsLocalSizeCalculation_BeforeTransferring()
    {
        var remote = new InMemoryRemote();
        var local = new InMemoryFileService();
        local.PutFile("a.txt", "content", Time(10));

        var reports = await SyncWithReportsAsync(remote, local, SyncMode.Push);

        Assert.StartsWith("Calculating local size", reports[0].Text);
    }

    [Fact]
    public async Task TwoWay_UnchangedFiles_AdvanceProgressAndCount()
    {
        var remote = new InMemoryRemote();
        remote.PutFile("/a.txt", "same", Time(10));
        remote.PutFile("/b.txt", "same", Time(10));
        var local = new InMemoryFileService();
        local.PutFile("a.txt", "same", Time(10));
        local.PutFile("b.txt", "same", Time(10));

        var reports = await SyncWithReportsAsync(remote, local, SyncMode.TwoWay);

        Assert.Contains(reports, r => r.Percent == 50);
        Assert.Equal(100, reports[^1].Percent);
        Assert.Contains("Files checked: 2", reports[^1].Text);
    }

    [Fact]
    public async Task Push_FailedUpload_StillCountsAsProcessed()
    {
        var remote = new InMemoryRemote();
        remote.HardFailUploads.Add("bad.txt");
        var local = new InMemoryFileService();
        local.PutFile("bad.txt", "content", Time(10));

        var reports = await SyncWithReportsAsync(remote, local, SyncMode.Push, failures: []);

        Assert.Equal(100, reports[^1].Percent);
    }

    /// <summary>Runs one sync under the English UI culture and returns every progress report.</summary>
    private static async Task<List<(string? Text, int Percent)>> SyncWithReportsAsync(
        InMemoryRemote remote, InMemoryFileService local, SyncMode mode, List<SyncFailure>? failures = null)
    {
        var reports = new List<(string? Text, int Percent)>();
        var previous = CultureInfo.CurrentUICulture;
        CultureInfo.CurrentUICulture = new CultureInfo("en");
        try
        {
            await ((ICloudConnection)remote).SyncAsync(
                local, User, RemoteRoot, LocalRoot, mode,
                new CloudSyncTransferOptions(new CloudSyncAdvancedSettings()), previousManifest: null,
                reportProgress: (text, percent) => reports.Add((text, percent)), failures: failures);
        }
        finally
        {
            CultureInfo.CurrentUICulture = previous;
        }
        return reports;
    }
}

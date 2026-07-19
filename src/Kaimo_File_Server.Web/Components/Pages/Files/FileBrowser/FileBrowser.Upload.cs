
using System.Collections;
using System.Diagnostics;
using Kaimo_File_Server.Core.Domain;
using Kaimo_File_Server.Core.Language;
using Kaimo_File_Server.Web.Components.Pages.Files.FileBrowser.components;
using Kaimo_File_Server.Web.Components.ViewModels;
using Kaimo_File_Server.Web.DynamicHelpers;
using Kaimo_File_Server.Web.Helpers;
using Kaimo_File_Server.Web.Services;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Forms;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.JSInterop;

namespace Kaimo_File_Server.Web.Components.Pages.Files.FileBrowser;

public partial class FileBrowser
{
// ========== File Uploading (UploadCoordinator + ProgressStream) ==========

    protected override void OnInitialized()
    {
        UploadCoordinator.OnFilesSelected += OnFileUploaded;
    }

    [JSInvokable]
    public void OnFilesDropped(int fileCount)
    {
        StateHasChanged();
    }

    private async Task OnFileUploaded(InputFileChangeEventArgs e)
    {
        var files = e.GetMultipleFiles(int.MaxValue);
        if (files.Count == 0) return;

        var cts = new CancellationTokenSource();
        long totalBytes = files.Sum(f => f.Size);
        long totalUploadedBytes = 0;
        int completedCount = 0;
        var failedFiles = new List<string>();

        var stopwatch = Stopwatch.StartNew();
        long lastSampleBytes = 0;
        TimeSpan lastSampleTime = TimeSpan.Zero;
        double currentSpeedBytesPerSec = 0;

        string toastId = null!;
        toastId = Toast.Show(
            BuildProgressText(files[0].Name, 0, files.Count, currentSpeedBytesPerSec),
            ToastType.Progress,
            onDismiss: () =>
            {
                cts.Cancel();
                Toast.Update(toastId, Resources.Web_Upload_BatchCancelled, type: ToastType.Error);
                return Task.CompletedTask;
            });

        foreach (var file in files)
        {
            if (cts.IsCancellationRequested)
                break;

            long fileBytesUploaded = 0;

            Stream stream;
            try
            {
                stream = file.OpenReadStream(maxAllowedSize: VM.GetMaxUploadSizeBytes());
            }
            catch (IOException)
            {
                failedFiles.Add(file.Name);
                completedCount++;
                continue;
            }

            try
            {
                await using (stream)
                {
                    var progressStream = new ProgressStream(stream, file.Size, (bytesRead, total) =>
                    {
                        totalUploadedBytes += bytesRead - fileBytesUploaded;
                        fileBytesUploaded = bytesRead;

                        var elapsedSinceSample = stopwatch.Elapsed - lastSampleTime;
                        if (elapsedSinceSample.TotalMilliseconds >= 500)
                        {
                            var bytesSinceSample = totalUploadedBytes - lastSampleBytes;
                            currentSpeedBytesPerSec = bytesSinceSample / elapsedSinceSample.TotalSeconds;

                            lastSampleBytes = totalUploadedBytes;
                            lastSampleTime = stopwatch.Elapsed;

                            var percent = totalBytes > 0 ? (int)(totalUploadedBytes * 100 / totalBytes) : 0;

                            InvokeAsync(() =>
                            {
                                Toast.Update(toastId,
                                    BuildProgressText(file.Name, completedCount, files.Count, currentSpeedBytesPerSec),
                                    progress: percent);
                                StateHasChanged();
                            });
                        }
                    });

                    var result = await VM.UploadFileAsync(file.Name, progressStream, cts.Token);

                    if (!result.Success)
                        failedFiles.Add(file.Name);
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (JSException)
            {
                Toast.Update(toastId, Resources.Web_Upload_PageLeft, type: ToastType.Error);
                cts.Dispose();
                return;
            }
            finally
            {
                completedCount++;
            }
        }

        stopwatch.Stop();

        if (!cts.IsCancellationRequested)
        {
            if (failedFiles.Count == 0)
            {
                Toast.Update(toastId,
                    files.Count == 1
                        ? string.Format(Resources.Web_Upload_Success, files[0].Name)
                        : string.Format(Resources.Web_Upload_BatchSuccess, files.Count),
                    type: ToastType.Success, progress: 100);
            }
            else
            {
                Toast.Update(toastId,
                    string.Format(Resources.Web_Upload_BatchPartialFailure,
                        files.Count - failedFiles.Count, files.Count),
                    type: ToastType.Error);
            }
        }

        cts.Dispose();

        await VM.LoadShareAsync(VM.CurrentShare!.Name, VM.CurrentPath);
        StateHasChanged();
    }


    private string BuildProgressText(string currentFileName, int completedCount, int totalCount, double speedBytesPerSec)
    {
        var speedText = FormatSpeed(speedBytesPerSec);
        return totalCount == 1
            ? string.Format(Resources.Web_Upload_ProgressWithSpeed, currentFileName, speedText)
            : string.Format(Resources.Web_Upload_BatchProgressWithSpeed, completedCount + 1, totalCount, currentFileName, speedText);
    }

    private static string FormatSpeed(double bytesPerSec)
    {
        if (bytesPerSec < 1024)
            return $"{bytesPerSec:F0} B/s";
        if (bytesPerSec < 1024 * 1024)
            return $"{bytesPerSec / 1024:F1} KB/s";
        return $"{bytesPerSec / (1024 * 1024):F1} MB/s";
    }

    private string BuildProgressText(string currentFileName, int completedCount, int totalCount)
    {
        return totalCount == 1
            ? string.Format(Resources.Web_Upload_Progress, currentFileName)
            : string.Format(Resources.Web_Upload_BatchProgress, completedCount + 1, totalCount, currentFileName);
    }
}


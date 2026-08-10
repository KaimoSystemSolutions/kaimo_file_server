
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
        if (VM.Capabilities.CanUpload)
            UploadCoordinator.OnFilesSelected += OnFileUploaded;
        if (VM.Capabilities.HasSearchIntegration)
            FileSelectionCoordinator.SelectionRequested += OnFileSelectionRequested;
    }

    [JSInvokable]
    public void OnFilesDropped(int fileCount)
    {
        StateHasChanged();
    }

    /// <summary>
    /// Uploads the selected browser files as one tracked, cancellable job. The
    /// job token is shared by the global job menu, toast dismissal callback, and
    /// underlying upload API so cancellation stops the actual transfer.
    /// </summary>
    private async Task OnFileUploaded(InputFileChangeEventArgs e)
    {
        var files = e.GetMultipleFiles(int.MaxValue);
        if (files.Count == 0) return;

        long totalBytes = files.Sum(f => f.Size);
        long totalUploadedBytes = 0;
        int completedCount = 0;
        var failedFiles = new List<string>();

        var stopwatch = Stopwatch.StartNew();
        long lastSampleBytes = 0;
        TimeSpan lastSampleTime = TimeSpan.Zero;
        double currentSpeedBytesPerSec = 0;

        // A single batch is represented as one job so cancelling it prevents the
        // current upload and every remaining file from starting.
        using var job = Jobs.Start(
            string.Format(
                Resources.ResourceManager.GetString("Web_Jobs_Upload_Title") ?? "Upload: {0}",
                files.Count == 1 ? files[0].Name : $"{files.Count} files"),
            BuildProgressText(files[0].Name, 0, files.Count, currentSpeedBytesPerSec),
            "upload");

        string toastId = null!;
        toastId = Toast.Show(
            BuildProgressText(files[0].Name, 0, files.Count, currentSpeedBytesPerSec),
            ToastType.Progress,
            onDismiss: () =>
            {
                job.Cancel();
                return Task.CompletedTask;
            });
        // Give the renderer a turn before opening/consuming the browser stream.
        // Otherwise a tiny upload can finish its transfer before the initial toast
        // ever becomes visible.
        await InvokeAsync(StateHasChanged);
        await Task.Yield();

        foreach (var file in files)
        {
            if (job.CancellationToken.IsCancellationRequested)
                break;

            long fileBytesUploaded = 0;
            bool processingShown = false;

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

                        var transferComplete = total == 0
                            ? bytesRead == 0
                            : bytesRead >= total;
                        if (transferComplete && !processingShown)
                        {
                            processingShown = true;
                            InvokeAsync(() =>
                            {
                                Toast.Update(
                                    toastId,
                                    BuildProcessingText(
                                        file.Name, completedCount, files.Count),
                                    progress: 100);
                                job.Update(
                                    BuildProcessingText(file.Name, completedCount, files.Count),
                                    100);
                                StateHasChanged();
                            });
                            return;
                        }

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
                                job.Update(
                                    BuildProgressText(file.Name, completedCount, files.Count, currentSpeedBytesPerSec),
                                    percent);
                                StateHasChanged();
                            });
                        }
                    });

                    var result = await VM.UploadFileAsync(
                        file.Name,
                        progressStream,
                        job.CancellationToken);

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
                return;
            }
            finally
            {
                completedCount++;
            }
        }

        stopwatch.Stop();

        if (job.CancellationToken.IsCancellationRequested)
        {
            Toast.Update(toastId, Resources.Web_Upload_BatchCancelled, type: ToastType.Error);
        }
        else
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

        await VM.LoadShareAsync(ShareName, VM.CurrentPath);
        StateHasChanged();
    }


    private string BuildProgressText(string currentFileName, int completedCount, int totalCount, double speedBytesPerSec)
    {
        var speedText = FormatSpeed(speedBytesPerSec);
        return totalCount == 1
            ? string.Format(Resources.Web_Upload_ProgressWithSpeed, currentFileName, speedText)
            : string.Format(Resources.Web_Upload_BatchProgressWithSpeed, completedCount + 1, totalCount, currentFileName, speedText);
    }

    private static string BuildProcessingText(
        string currentFileName, int completedCount, int totalCount)
    {
        return totalCount == 1
            ? string.Format(Resources.Web_Upload_Processing, currentFileName)
            : string.Format(
                Resources.Web_Upload_BatchProcessing,
                completedCount + 1, totalCount, currentFileName);
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


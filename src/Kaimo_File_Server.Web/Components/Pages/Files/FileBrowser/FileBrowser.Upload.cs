
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
    private Task OnFileUploaded(InputFileChangeEventArgs e, Guid batchId)
    {
        // Do not retain the input-change event for the entire batch. In Blazor
        // Server that can serialize user interactions behind a long upload.
        // BrowserFile streams remain valid because MainLayout keeps this batch's
        // input element alive until ProcessFileUploadAsync signals completion.
        _ = ProcessFileUploadAsync(e, batchId);
        return Task.CompletedTask;
    }

    private async Task ProcessFileUploadAsync(InputFileChangeEventArgs e, Guid batchId)
    {
        try
        {
            var files = e.GetMultipleFiles(int.MaxValue);
            if (files.Count == 0) return;

        // Name-collision handling is a local-share feature; remote/cloud browsers keep
        // their own overwrite semantics and fall back to the plain upload call below.
        var local = VM as FileBrowserViewModel;
        local?.BeginUploadBatch();

        long totalBytes = files.Sum(f => f.Size);
        long totalUploadedBytes = 0;
        int completedCount = 0;
        int skippedCount = 0;   // identical content, discarded silently
        int heldCount = 0;      // waiting for the user's conflict decision
        var failedFiles = new List<string>();
        var failureReasons = new List<string>();

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

                    if (local is not null)
                    {
                        var outcome = await local.UploadWithConflictHandlingAsync(
                            file.Name, progressStream, job.CancellationToken);
                        switch (outcome)
                        {
                            case FileBrowserViewModel.UploadResult.SkippedIdentical:
                                skippedCount++;
                                break;
                            case FileBrowserViewModel.UploadResult.Held:
                                heldCount++;
                                break;
                            case FileBrowserViewModel.UploadResult.Failed:
                                failedFiles.Add(file.Name);
                                break;
                            // Uploaded / Discarded need no per-file bookkeeping.
                        }
                    }
                    else
                    {
                        var result = await VM.UploadFileAsync(
                            file.Name, progressStream, job.CancellationToken);
                        if (!result.Success)
                        {
                            failedFiles.Add(file.Name);
                            if (!string.IsNullOrWhiteSpace(result.Error))
                                failureReasons.Add(result.Error);
                        }
                    }
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

        // Held files are neither done nor failed — the conflict dialog resolves them.
        var notes = new List<string>();
        if (skippedCount > 0)
            notes.Add(string.Format(T("Web_Upload_SkippedIdentical"), skippedCount));
        if (heldCount > 0)
            notes.Add(string.Format(T("Web_Upload_ConflictsPending"), heldCount));
        var suffix = notes.Count > 0 ? " " + string.Join(" ", notes) : "";

        if (job.CancellationToken.IsCancellationRequested)
        {
            Toast.Update(toastId, Resources.Web_Upload_BatchCancelled, type: ToastType.Error);
        }
        else if (failedFiles.Count == 0)
        {
            var uploaded = files.Count - skippedCount - heldCount;
            string baseMsg = uploaded <= 0
                ? notes.FirstOrDefault() ?? string.Format(Resources.Web_Upload_BatchSuccess, 0)
                : files.Count == 1
                    ? string.Format(Resources.Web_Upload_Success, files[0].Name)
                    : string.Format(Resources.Web_Upload_BatchSuccess, uploaded);
            // If the only outcome note is already the whole message, don't repeat it.
            var text = uploaded <= 0 ? baseMsg : (baseMsg + suffix);
            Toast.Update(toastId, text.Trim(),
                type: heldCount > 0 ? ToastType.Info : ToastType.Success, progress: 100);
        }
        else
        {
            string summary = string.Format(Resources.Web_Upload_BatchPartialFailure,
                files.Count - failedFiles.Count, files.Count);
            string? reason = failureReasons.Distinct(StringComparer.CurrentCulture).FirstOrDefault();
            Toast.Update(toastId,
                (files.Count == 1 && reason is not null
                    ? reason
                    : reason is null ? summary : $"{summary} {reason}") + suffix,
                type: ToastType.Error);
        }

            await VM.LoadShareAsync(ShareName, VM.CurrentPath);
            StateHasChanged();
        }
        catch (Exception)
        {
            // The upload now runs independently from the input-change event, so
            // surface unexpected failures here instead of leaving an unobserved task.
            Toast.Show(Resources.Web_Upload_Failed, ToastType.Error);
        }
        finally
        {
            // A "apply to all" conflict choice stays in effect only while a batch runs.
            (VM as FileBrowserViewModel)?.EndUploadBatch();

            // This batch's streams are fully consumed; let the layout release its
            // input element. Other in-flight batches keep their own elements.
            UploadCoordinator.NotifyFilesProcessed(batchId);
        }
    }


    // Refresh the listing after a held upload conflict is resolved (a write may have
    // added or replaced a file).
    private async Task OnConflictResolved()
    {
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


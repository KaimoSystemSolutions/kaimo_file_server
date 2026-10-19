
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

    // Folder structure of the next selection. IBrowserFile only carries the leaf name, so
    // fileUpload.js registers the folder of every file (same order as the files) right
    // BEFORE it hands the files to the global input; the next batch takes it from here.
    private sealed record PendingUploadPaths(string?[] Dirs, string[] EmptyDirs);
    private PendingUploadPaths? _pendingUploadPaths;

    // Empty arrays withdraw a registration whose files never reached the input.
    [JSInvokable]
    public void SetPendingUploadPaths(string[] dirs, int[] dirIndex, string[] emptyDirs)
        => _pendingUploadPaths = dirIndex.Length == 0 && emptyDirs.Length == 0
            ? null
            : new PendingUploadPaths(UploadFolderPath.Unpack(dirs, dirIndex), emptyDirs);

    [JSInvokable]
    public void OnUploadTooManyFiles(int maxFiles)
        => ShowUploadError(string.Format(T("Web_Upload_TooManyFiles"), maxFiles));

    [JSInvokable]
    public void OnUploadSelectionTooLarge()
        => ShowUploadError(T("Web_Upload_SelectionTooLarge"));

    private void ShowUploadError(string message)
        => InvokeAsync(() =>
        {
            Toast.Show(message, ToastType.Error);
            StateHasChanged();
        });

    private Task OpenFileUploadDialog()
    {
        _showUploadMenu = false;
        UploadCoordinator.RequestUpload();
        return Task.CompletedTask;
    }

    private async Task OpenFolderUploadDialog()
    {
        _showUploadMenu = false;
        _dotNetRef ??= DotNetObjectReference.Create(this);
        await JS.InvokeVoidAsync("kaimoUpload.pickFolder", _dotNetRef);
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

            // Paths only belong to this selection when they line up with its files; a plain
            // file-dialog selection never registers any and uploads flat as before.
            var pending = Interlocked.Exchange(ref _pendingUploadPaths, null);
            var folderUpload = pending is not null && pending.Dirs.Length == files.Count;
            var emptyDirs = folderUpload ? pending!.EmptyDirs : [];
            if (files.Count == 0)
            {
                // A drop of nothing but empty folders.
                if (emptyDirs.Length > 0)
                {
                    await CreateUploadFoldersAsync(emptyDirs, new HashSet<string>(StringComparer.OrdinalIgnoreCase), CancellationToken.None);
                    await VM.LoadShareAsync(ShareName, EffectiveLoadSubPath);
                    await InvokeAsync(StateHasChanged);
                }
                return;
            }

            // The folder of each file below the drop target ("" = the target itself), or
            // null when the client sent an invalid one (the file then fails).
            // Names Windows cannot use are renamed on the way in; count them for the summary.
            var fileDirs = new string?[files.Count];
            var renamedCount = 0;
            for (var i = 0; i < files.Count; i++)
            {
                var dirRenamed = false;
                fileDirs[i] = !folderUpload ? ""
                    : pending!.Dirs[i] is { } raw && UploadFolderPath.TryNormalize(raw, out var dir, out dirRenamed) ? dir : null;
                if (dirRenamed || (VM is FileBrowserViewModel && !WindowsFileNameHelper.IsValid(files[i].Name)))
                    renamedCount++;
            }

        // Name-collision handling is a local-share feature; remote/cloud browsers keep
        // their own overwrite semantics and fall back to the plain upload call below.
        var local = VM as FileBrowserViewModel;
        local?.BeginUploadBatch();

        // Folders created (or found) during this batch, and ones that could not be.
        var readyDirs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var failedDirs = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        string DisplayName(int i) => string.IsNullOrEmpty(fileDirs[i]) ? files[i].Name : $"{fileDirs[i]}/{files[i].Name}";

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
                BuildBatchLabel(files, fileDirs)),
            BuildProgressText(DisplayName(0), 0, files.Count, currentSpeedBytesPerSec),
            "upload");

        string toastId = null!;
        toastId = Toast.Show(
            BuildProgressText(DisplayName(0), 0, files.Count, currentSpeedBytesPerSec),
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

        for (var index = 0; index < files.Count; index++)
        {
            if (job.CancellationToken.IsCancellationRequested)
                break;

            var file = files[index];
            var relativeDir = fileDirs[index];
            var displayName = DisplayName(index);
            long fileBytesUploaded = 0;
            bool processingShown = false;

            // Folder uploads: the file's folder must exist before the file can land in it.
            string? folderError;
            try
            {
                folderError = relativeDir is null
                    ? T("Web_Upload_InvalidFolderName")
                    : await EnsureUploadFolderAsync(relativeDir, readyDirs, failedDirs, job.CancellationToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            if (folderError is not null)
            {
                failedFiles.Add(displayName);
                failureReasons.Add(folderError);
                totalUploadedBytes += file.Size;
                completedCount++;
                continue;
            }

            Stream stream;
            try
            {
                stream = file.OpenReadStream(maxAllowedSize: VM.GetMaxUploadSizeBytes());
            }
            catch (IOException)
            {
                failedFiles.Add(displayName);
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
                                        displayName, completedCount, files.Count),
                                    progress: 100);
                                job.Update(
                                    BuildProcessingText(displayName, completedCount, files.Count),
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
                                    BuildProgressText(displayName, completedCount, files.Count, currentSpeedBytesPerSec),
                                    progress: percent);
                                job.Update(
                                    BuildProgressText(displayName, completedCount, files.Count, currentSpeedBytesPerSec),
                                    percent);
                                StateHasChanged();
                            });
                        }
                    });

                    if (local is not null)
                    {
                        var outcome = await local.UploadWithConflictHandlingAsync(
                            file.Name, progressStream, job.CancellationToken, relativeDir,
                            file.LastModified.UtcDateTime);
                        switch (outcome)
                        {
                            case FileBrowserViewModel.UploadResult.SkippedIdentical:
                                skippedCount++;
                                break;
                            case FileBrowserViewModel.UploadResult.Held:
                                heldCount++;
                                break;
                            case FileBrowserViewModel.UploadResult.Failed:
                                failedFiles.Add(displayName);
                                break;
                            // Uploaded / Discarded need no per-file bookkeeping.
                        }
                    }
                    else
                    {
                        var result = await VM.UploadFileAsync(
                            file.Name, progressStream, job.CancellationToken, relativeDir);
                        if (!result.Success)
                        {
                            failedFiles.Add(displayName);
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

        // Empty folders carry no file that would have created them on the way.
        if (!job.CancellationToken.IsCancellationRequested && emptyDirs.Length > 0)
            await CreateUploadFoldersAsync(emptyDirs, readyDirs, job.CancellationToken);

        // Held files are neither done nor failed — the conflict dialog resolves them.
        var notes = new List<string>();
        if (skippedCount > 0)
            notes.Add(string.Format(T("Web_Upload_SkippedIdentical"), skippedCount));
        if (heldCount > 0)
            notes.Add(string.Format(T("Web_Upload_ConflictsPending"), heldCount));
        if (renamedCount > 0)
            notes.Add(string.Format(T("Web_Upload_RenamedForWindows"), renamedCount));
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
                    ? string.Format(Resources.Web_Upload_Success, DisplayName(0))
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

            await VM.LoadShareAsync(ShareName, EffectiveLoadSubPath);
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


    /// <summary>
    /// Makes sure a folder-upload target folder exists. Returns null when it does, else the
    /// error; each folder is checked once per batch and a failure is remembered so the
    /// remaining files in it fail fast with the same reason.
    /// </summary>
    private async Task<string?> EnsureUploadFolderAsync(
        string relativeDir, HashSet<string> readyDirs, Dictionary<string, string> failedDirs,
        CancellationToken cancellationToken)
    {
        if (relativeDir.Length == 0 || readyDirs.Contains(relativeDir))
            return null;
        if (failedDirs.TryGetValue(relativeDir, out var known))
            return known;

        var result = await VM.EnsureFolderAsync(relativeDir, cancellationToken);
        if (result.Success)
        {
            readyDirs.Add(relativeDir);
            return null;
        }

        var error = result.Error ?? Resources.Web_Error_CreateFolderFailed;
        failedDirs[relativeDir] = error;
        return error;
    }

    private async Task CreateUploadFoldersAsync(
        IEnumerable<string> rawDirs, HashSet<string> readyDirs, CancellationToken cancellationToken)
    {
        var failedDirs = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            foreach (var raw in rawDirs)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!UploadFolderPath.TryNormalize(raw, out var dir)) continue;
                if (await EnsureUploadFolderAsync(dir, readyDirs, failedDirs, cancellationToken) is { } error)
                    Toast.Show(error, ToastType.Error);
            }
        }
        catch (OperationCanceledException)
        {
            // The batch reports itself as cancelled.
        }
    }

    // "report.pdf", "Photos (120 files)" for a single dropped folder, or "37 files".
    private static string BuildBatchLabel(IReadOnlyList<IBrowserFile> files, string?[] fileDirs)
    {
        if (files.Count == 1 && string.IsNullOrEmpty(fileDirs[0]))
            return files[0].Name;

        var count = string.Format(T("Web_Upload_FilesCount"), files.Count);
        string? root = null;
        foreach (var dir in fileDirs)
        {
            var top = string.IsNullOrEmpty(dir) ? null : dir.Split('/')[0];
            if (top is null || (root is not null && !string.Equals(root, top, StringComparison.OrdinalIgnoreCase)))
                return count;
            root = top;
        }

        return $"{root} ({count})";
    }

    // Refresh the listing after a held upload conflict is resolved (a write may have
    // added or replaced a file).
    private async Task OnConflictResolved()
    {
        await VM.LoadShareAsync(ShareName, EffectiveLoadSubPath);
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


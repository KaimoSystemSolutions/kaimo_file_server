using Kaimo_File_Server.Core.Domain.Identity;
using Kaimo_File_Server.Core.Services.File;
using Kaimo_File_Server.Infrastructure.Clouds;

namespace Kaimo_File_Server.Core.Services.DataServices;

/// <summary>
/// Provider-neutral cloud storage contract used by the synchronization engine.
/// Implementations must honor cancellation tokens for network-bound operations
/// so a UI cancellation stops real work rather than only hiding its progress.
/// </summary>
public interface ICloudConnection
{
    Task Dispose();
    string ServiceName { get; }

    /// <summary>
    /// Indicates that the provider rotated persisted credentials while the
    /// connection was in use. Callers should save the owning SyncedFolder and
    /// acknowledge the change only after persistence succeeded.
    /// </summary>
    bool HasPendingCredentialChanges => false;

    /// <summary>
    /// Returns only the credential fields that changed at runtime. This allows
    /// callers to merge them into a freshly loaded aggregate without overwriting
    /// concurrent edits to paths or sync settings.
    /// </summary>
    IReadOnlyDictionary<string, string> GetPendingCredentialChanges()
        => new Dictionary<string, string>();

    /// <summary>Marks provider credential changes as successfully persisted.</summary>
    void AcknowledgeCredentialChanges()
    {
    }

    /// <summary>
    /// Returns optional account metadata without exposing a provider-specific SDK type.
    /// The default keeps providers that do not offer account profiles lightweight.
    /// </summary>
    Task<CloudAccountInfo?> GetAccountInfoAsync() => Task.FromResult<CloudAccountInfo?>(null);

    /// <summary>Creates or replaces a remote file while preserving its source timestamp.</summary>
    Task UploadAsync(
        string path,
        Stream data,
        DateTime modifiedTime,
        CancellationToken cancellationToken = default);
    /// <summary>Streams a remote file into the supplied target stream.</summary>
    Task DownloadAsync(
        string path,
        Stream target,
        CancellationToken cancellationToken = default);
    /// <summary>Creates a remote directory, including missing parents where supported.</summary>
    Task CreateDirectoryAsync(string path, CancellationToken cancellationToken = default);

    /// <summary>Recursively calculates the total size used for sync progress reporting.</summary>
    Task<long> GetDirectorySizeAsync(string path, CancellationToken cancellationToken = default);
    
    /// <summary>Lists the direct children of a remote directory.</summary>
    Task<IReadOnlyList<CloudItemMeta>> ListAsync(
        string path,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Synchronizes one configured local/remote directory pair. Cancellation is
    /// checked between local file-service calls and is forwarded to every cloud
    /// operation so long-running uploads and downloads can stop immediately.
    /// </summary>
    async Task SyncAsync(
        IFileService fileService,
        UserContext user,
        string remotePath,
        string localPath,
        SyncMode mode,
        Action<string?, int>? reportProgress = null,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        long totalBytes = mode switch
        {
            SyncMode.Push => await fileService.GetDirectorySizeAsync(localPath, user),
            SyncMode.Pull => await GetDirectorySizeAsync(remotePath, cancellationToken),
            SyncMode.TwoWay => Math.Max(
                await fileService.GetDirectorySizeAsync(localPath, user),
                await GetDirectorySizeAsync(remotePath, cancellationToken)),
            _ => 0
        };

        cancellationToken.ThrowIfCancellationRequested();
        
        var progress = new SyncProgress
        {
            TotalBytes = totalBytes,
            Report = reportProgress
        };

        progress.Update("Scanning...");

        await SyncDirectory(
            fileService,
            user,
            remotePath.TrimEnd('/'),
            localPath.TrimEnd('/'),
            mode,
            progress,
            cancellationToken
            );
    }
    
    /// <summary>
    /// Compares timestamps with a small tolerance because cloud providers often
    /// round modification times differently from the local file system.
    /// </summary>
    private static int CompareModifiedTime(
        DateTime local,
        DateTime remote)
    {
        var difference = local - remote;

        if (Math.Abs(difference.TotalMilliseconds) < 5)
            return 0;

        return difference > TimeSpan.Zero ? 1 : -1;
    }
    /// <summary>
    /// Recursively reconciles one directory level according to the selected sync
    /// mode and checks cancellation before each child is processed.
    /// </summary>
    private async Task SyncDirectory(
        IFileService fileService,
        UserContext user,
        string remoteDir,
        string localDir,
        SyncMode mode,
        SyncProgress syncProgress,
        CancellationToken cancellationToken
        )
    {
        cancellationToken.ThrowIfCancellationRequested();
        var remoteItems = (await ListAsync(remoteDir, cancellationToken))
            .ToDictionary(x => x.Name, StringComparer.OrdinalIgnoreCase);

        var localItems = (await fileService.ListAsync(localDir, user))
            .ToDictionary(x => x.Name, StringComparer.OrdinalIgnoreCase);
        cancellationToken.ThrowIfCancellationRequested();

        var allNames = new HashSet<string>(
            remoteItems.Keys.Concat(localItems.Keys),
            StringComparer.OrdinalIgnoreCase);

        foreach (var name in allNames)
        {
            cancellationToken.ThrowIfCancellationRequested();
            remoteItems.TryGetValue(name, out var remote);
            localItems.TryGetValue(name, out var local);

            bool remoteExists = remote != null;
            bool localExists = local != null;

            string remoteChild = $"{remoteDir}/{name}";
            string localChild = $"{localDir}/{name}";

            //--------------------------------------------------
            // Only local
            //--------------------------------------------------
            if (localExists && !remoteExists)
            {
                if (mode == SyncMode.Pull)
                    continue;

                if (local!.IsDirectory)
                {
                    await CreateDirectoryAsync(remoteChild, cancellationToken);

                    await UploadDirectoryRecursive(
                        fileService,
                        user,
                        localChild,
                        remoteChild,
                        syncProgress,
                        cancellationToken);
                }
                else
                {
                    syncProgress.Update($"Pushing {local.Name}...");

                    await using var stream = await fileService.ReadFileAsync(localChild, user);
                    await UploadAsync(remoteChild, stream, local.ModifiedAt, cancellationToken);

                    syncProgress.TransferredBytes += local.Size;
                    syncProgress.Update($"Pushing {local.Name}...");
                }

                continue;
            }

            //--------------------------------------------------
            // Only remote
            //--------------------------------------------------
            if (!localExists && remoteExists)
            {
                if (mode == SyncMode.Push)
                    continue;

                if (remote!.IsDirectory)
                {
                    await fileService.CreateDirectoryAsync(localChild, user);
                    cancellationToken.ThrowIfCancellationRequested();

                    await SyncDirectory(
                        fileService,
                        user,
                        remoteChild,
                        localChild,
                        mode,
                        syncProgress,
                        cancellationToken);
                }
                else
                {
                    syncProgress.Update($"Pulling {remote.Name}...");

                    await using var ms = new MemoryStream();
                    await DownloadAsync(remoteChild, ms, cancellationToken);

                    ms.Position = 0;
                    cancellationToken.ThrowIfCancellationRequested();
                    await fileService.WriteFileAsync(localChild, ms, user);
                    cancellationToken.ThrowIfCancellationRequested();
                    await fileService.SetModifiedAtAsync(localChild, user, remote.ModifiedAt);
                    
                    syncProgress.TransferredBytes += remote.Size;
                    syncProgress.Update($"Pulling {remote.Name}...");
                }

                continue;
            }

            //--------------------------------------------------
            // Both directories
            //--------------------------------------------------
            if (local!.IsDirectory && remote!.IsDirectory)
            {
                await SyncDirectory(
                    fileService,
                    user,
                    remoteChild,
                    localChild,
                    mode,
                    syncProgress,
                    cancellationToken);

                continue;
            }

            if (local.IsDirectory != remote.IsDirectory)
                continue;

            //--------------------------------------------------
            // Pull
            //--------------------------------------------------
            if (mode == SyncMode.Pull)
            {
                if (CompareModifiedTime(local.ModifiedAt, remote.ModifiedAt) < 0)
                {
                    syncProgress.Update($"Pulling {remote.Name}...");

                    await using var ms = new MemoryStream();
                    await DownloadAsync(remoteChild, ms, cancellationToken);

                    ms.Position = 0;
                    cancellationToken.ThrowIfCancellationRequested();
                    await fileService.WriteFileAsync(localChild, ms, user);
                    cancellationToken.ThrowIfCancellationRequested();
                    await fileService.SetModifiedAtAsync(localChild, user, remote.ModifiedAt);

                    syncProgress.TransferredBytes += remote.Size;
                    syncProgress.Update($"Pulling {remote.Name}...");
                }

                continue;
            }

            //--------------------------------------------------
            // Push
            //--------------------------------------------------
            if (mode == SyncMode.Push)
            {
                if (CompareModifiedTime(local.ModifiedAt, remote.ModifiedAt) > 0)
                {
                    syncProgress.Update($"Pushing {local.Name}...");

                    await using var stream = await fileService.ReadFileAsync(localChild, user);
                    await UploadAsync(remoteChild, stream, local.ModifiedAt, cancellationToken);

                    syncProgress.TransferredBytes += local.Size;
                    syncProgress.Update($"Pushing {local.Name}...");
                }

                continue;
            }

            //--------------------------------------------------
            // Two-way
            //--------------------------------------------------
            if (CompareModifiedTime(local.ModifiedAt, remote.ModifiedAt) > 0)
            {
                syncProgress.Update($"Pushing {local.Name}...");

                await using var stream = await fileService.ReadFileAsync(localChild, user);
                await UploadAsync(remoteChild, stream, local.ModifiedAt, cancellationToken);

                syncProgress.TransferredBytes += local.Size;
                syncProgress.Update($"Pushing {local.Name}...");
            }
            else if (CompareModifiedTime(local.ModifiedAt, remote.ModifiedAt) < 0)
            {
                syncProgress.Update($"Pulling {remote.Name}...");

                await using var ms = new MemoryStream();
                await DownloadAsync(remoteChild, ms, cancellationToken);

                ms.Position = 0;
                cancellationToken.ThrowIfCancellationRequested();
                await fileService.WriteFileAsync(localChild, ms, user);
                cancellationToken.ThrowIfCancellationRequested();
                await fileService.SetModifiedAtAsync(localChild, user, remote.ModifiedAt);
                
                syncProgress.TransferredBytes += remote.Size;
                syncProgress.Update($"Pulling {remote.Name}...");
            }
        }
    }

    /// <summary>
    /// Uploads a local-only directory tree while forwarding progress and the
    /// cancellation token into each provider call.
    /// </summary>
    private async Task UploadDirectoryRecursive(
        IFileService fileService,
        UserContext user,
        string localDir,
        string remoteDir,
        SyncProgress syncProgress,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await CreateDirectoryAsync(remoteDir, cancellationToken);

        foreach (var item in await fileService.ListAsync(localDir, user))
        {
            cancellationToken.ThrowIfCancellationRequested();
            string localChild = $"{localDir}/{item.Name}";
            string remoteChild = $"{remoteDir}/{item.Name}";

            if (item.IsDirectory)
            {
                await UploadDirectoryRecursive(
                    fileService,
                    user,
                    localChild,
                    remoteChild,
                    syncProgress,
                    cancellationToken);
            }
            else
            {
                syncProgress.Update($"Pushing {item.Name}");

                await using var stream = await fileService.ReadFileAsync(localChild, user);
                await UploadAsync(remoteChild, stream, item.ModifiedAt, cancellationToken);

                syncProgress.TransferredBytes += item.Size;
                syncProgress.Update($"Pushing {item.Name}");
            }
        }
    }
    
    private sealed class SyncProgress
    {
        public long TotalBytes { get; init; }
        public long TransferredBytes { get; set; }

        public Action<string, int>? Report { get; init; }

        public void Update(string text)
        {
            if (Report == null)
                return;

            int percent = TotalBytes == 0
                ? 100
                : (int)Math.Clamp(TransferredBytes * 100 / TotalBytes, 0, 100);

            Report(text, percent);
        }
    }
    
}

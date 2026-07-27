using Kaimo_File_Server.Core.Domain.Identity;
using Kaimo_File_Server.Core.Services.File;
using Kaimo_File_Server.Infrastructure.Clouds;

namespace Kaimo_File_Server.Core.Services.DataServices;

public interface ICloudConnection
{
    Task Dispose();
    string getServiceName();
    Task UploadAsync(string path, Stream data, DateTime modifiedTime);
    Task DownloadAsync(string path, Stream target);
    Task CreateDirectoryAsync(string path);
    Task<long> GetDirectorySizeAsync(string path);
    
    Task<IReadOnlyList<CloudItemMeta>> ListAsync(string path);

    async Task SyncAsync(
        IFileService fileService,
        UserContext user,
        string remotePath,
        string localPath,
        SyncMode mode,
        Action<string?, int>? reportProgress = null)
    {
        long totalBytes = mode switch
        {
            SyncMode.Push => await fileService.GetDirectorySizeAsync(localPath, user),
            SyncMode.Pull => await GetDirectorySizeAsync(remotePath),
            SyncMode.TwoWay => Math.Max(
                await fileService.GetDirectorySizeAsync(localPath, user),
                await GetDirectorySizeAsync(remotePath)),
            _ => 0
        };
        
        
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
            progress
            );
    }
    
    private static int CompareModifiedTime(
        DateTime local,
        DateTime remote)
    {
        var difference = local - remote;

        if (Math.Abs(difference.TotalMilliseconds) < 5)
            return 0;

        return difference > TimeSpan.Zero ? 1 : -1;
    }
    private async Task SyncDirectory(
        IFileService fileService,
        UserContext user,
        string remoteDir,
        string localDir,
        SyncMode mode,
        SyncProgress syncProgress
        )
    {
        var remoteItems = (await ListAsync(remoteDir))
            .ToDictionary(x => x.Name, StringComparer.OrdinalIgnoreCase);

        var localItems = (await fileService.ListAsync(localDir, user))
            .ToDictionary(x => x.Name, StringComparer.OrdinalIgnoreCase);

        var allNames = new HashSet<string>(
            remoteItems.Keys.Concat(localItems.Keys),
            StringComparer.OrdinalIgnoreCase);

        foreach (var name in allNames)
        {
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
                    await CreateDirectoryAsync(remoteChild);

                    await UploadDirectoryRecursive(
                        fileService,
                        user,
                        localChild,
                        remoteChild,
                        syncProgress);
                }
                else
                {
                    syncProgress.Update($"Pushing {local.Name}...");

                    await using var stream = await fileService.ReadFileAsync(localChild, user);
                    await UploadAsync(remoteChild, stream, local.ModifiedAt);

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

                    await SyncDirectory(
                        fileService,
                        user,
                        remoteChild,
                        localChild,
                        mode,
                        syncProgress);
                }
                else
                {
                    syncProgress.Update($"Pulling {remote.Name}...");

                    await using var ms = new MemoryStream();
                    await DownloadAsync(remoteChild, ms);

                    ms.Position = 0;
                    await fileService.WriteFileAsync(localChild, ms, user);
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
                    syncProgress);

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
                    await DownloadAsync(remoteChild, ms);

                    ms.Position = 0;
                    await fileService.WriteFileAsync(localChild, ms, user);
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
                    await UploadAsync(remoteChild, stream, local.ModifiedAt);

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
                await UploadAsync(remoteChild, stream, local.ModifiedAt);

                syncProgress.TransferredBytes += local.Size;
                syncProgress.Update($"Pushing {local.Name}...");
            }
            else if (CompareModifiedTime(local.ModifiedAt, remote.ModifiedAt) < 0)
            {
                syncProgress.Update($"Pulling {remote.Name}...");

                await using var ms = new MemoryStream();
                await DownloadAsync(remoteChild, ms);

                ms.Position = 0;
                await fileService.WriteFileAsync(localChild, ms, user);
                await fileService.SetModifiedAtAsync(localChild, user, remote.ModifiedAt);
                
                syncProgress.TransferredBytes += remote.Size;
                syncProgress.Update($"Pulling {remote.Name}...");
            }
        }
    }

    private async Task UploadDirectoryRecursive(
        IFileService fileService,
        UserContext user,
        string localDir,
        string remoteDir,
        SyncProgress syncProgress)
    {
        await CreateDirectoryAsync(remoteDir);

        foreach (var item in await fileService.ListAsync(localDir, user))
        {
            string localChild = $"{localDir}/{item.Name}";
            string remoteChild = $"{remoteDir}/{item.Name}";

            if (item.IsDirectory)
            {
                await UploadDirectoryRecursive(
                    fileService,
                    user,
                    localChild,
                    remoteChild,
                    syncProgress);
            }
            else
            {
                syncProgress.Update($"Pushing {item.Name}");

                await using var stream = await fileService.ReadFileAsync(localChild, user);
                await UploadAsync(remoteChild, stream, item.ModifiedAt);

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
using Kaimo_File_Server.Core.Domain.Identity;
using Kaimo_File_Server.Core.Repositories;
using Kaimo_File_Server.Core.Services;
using Kaimo_File_Server.Core.Services.DataServices;
using Kaimo_File_Server.Core.Services.File;

namespace Kaimo_File_Server.Infrastructure.Clouds;

/// <summary>
/// UI-independent entry point shared by manual syncs and future scheduled jobs.
/// It owns operation coordination, fresh configuration loading, provider
/// execution, and narrow runtime-state persistence.
/// </summary>
public interface ICloudSyncExecutionService
{
    Task<CloudSyncExecutionResult> RunAsync(
        Guid shareId,
        string localPath,
        UserContext actor,
        Action<string?, int>? reportProgress = null,
        CancellationToken cancellationToken = default);
}

public enum CloudSyncExecutionResult
{
    Completed,
    Busy,
    Missing
}

public sealed class CloudSyncExecutionService(
    IShareRepository shares,
    ICloudProviderFactory providers,
    IFileServiceFactory fileServices,
    ICloudSyncOperationCoordinator operations) : ICloudSyncExecutionService
{
    public async Task<CloudSyncExecutionResult> RunAsync(
        Guid shareId,
        string localPath,
        UserContext actor,
        Action<string?, int>? reportProgress = null,
        CancellationToken cancellationToken = default)
    {
        string normalizedPath = CloudSyncPaths.Normalize(localPath);
        var operationLease = await operations.TryBeginSyncAsync(
            shareId, normalizedPath, cancellationToken);
        if (operationLease is null)
            return CloudSyncExecutionResult.Busy;

        await using (operationLease)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var share = await shares.GetByIdAsync(shareId);
            if (share is null ||
                !share.CloudSettings.Folders.TryGetValue(
                    normalizedPath, out var folder))
                return CloudSyncExecutionResult.Missing;

            var connection = providers.CreateOrLoad(share.Id, folder);
            var fileService = fileServices.CreateForShare(share.Id, share.Path);
            await connection.SyncAsync(
                fileService,
                actor,
                NormalizeRemotePath(folder.RemotePath),
                normalizedPath,
                folder.Mode,
                new CloudSyncTransferOptions(folder.AdvancedSettings),
                reportProgress,
                cancellationToken);

            cancellationToken.ThrowIfCancellationRequested();
            var credentialChanges = connection.HasPendingCredentialChanges
                ? connection.GetPendingCredentialChanges()
                : new Dictionary<string, string>();
            bool persisted = await shares.UpdateCloudSyncRuntimeStateAsync(
                share.Id,
                normalizedPath,
                DateTime.UtcNow,
                credentialChanges);
            if (!persisted)
                return CloudSyncExecutionResult.Missing;

            if (connection.HasPendingCredentialChanges)
                connection.AcknowledgeCredentialChanges();
            return CloudSyncExecutionResult.Completed;
        }
    }

    private static string NormalizeRemotePath(string? path)
    {
        string normalized = string.IsNullOrWhiteSpace(path)
            ? ""
            : path.Replace('\\', '/').Trim('/');
        return normalized.Length == 0 ? "/" : $"/{normalized}";
    }
}

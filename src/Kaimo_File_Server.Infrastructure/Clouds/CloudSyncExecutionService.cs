using Kaimo_File_Server.Core.Domain;
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
    ISyncDefinitionRepository syncDefinitions,
    IStorageConnectionRepository storageConnections,
    ICredentialVault credentialVault,
    ILegacyCloudSyncMigrationService legacyMigration,
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
            await legacyMigration.EnsureMigratedAsync(cancellationToken);
            var share = await shares.GetByIdAsync(shareId);
            var definition = await syncDefinitions.GetBySharePathAsync(
                shareId, normalizedPath, cancellationToken);
            if (share is null || definition is null || !definition.Enabled)
                return CloudSyncExecutionResult.Missing;

            var storageConnection = await storageConnections.GetAsync(
                definition.ConnectionId, cancellationToken);
            if (storageConnection is null ||
                storageConnection.State is StorageConnectionState.Disabled or
                    StorageConnectionState.NeedsReauthorization)
                return CloudSyncExecutionResult.Missing;

            Dictionary<string, string> credentials =
                credentialVault.UnprotectConnectionCredentials(storageConnection);
            // The stable ID prevents refresh-token rotation from creating a new
            // live provider cache entry. Providers ignore this neutral key.
            credentials["connectionId"] = storageConnection.Id.ToString("D");
            var folder = new SyncedFolder(
                storageConnection.ProviderId,
                credentials,
                definition.RemotePath)
            {
                DisplayName = definition.DisplayName,
                Description = definition.Description,
                RequiresRemoteFolderSelection = definition.RequiresRemoteFolderSelection,
                Mode = definition.Mode,
                Schedule = definition.Schedule,
                AdvancedSettings = definition.AdvancedSettings
            };

            var connection = providers.CreateOrLoad(share.Id, folder);
            var fileService = fileServices.CreateForShare(share.Id, share.Path);
            try
            {
                await connection.SyncAsync(
                    fileService,
                    actor,
                    NormalizeRemotePath(folder.RemotePath),
                    normalizedPath,
                    folder.Mode,
                    new CloudSyncTransferOptions(folder.AdvancedSettings),
                    reportProgress,
                    cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                string errorCode = exception is ProviderRequestException providerError
                    ? providerError.ErrorCode
                    : "sync_failed";
                await syncDefinitions.MarkFailedAsync(
                    definition.Id,
                    DateTime.UtcNow,
                    errorCode,
                    cancellationToken);
                throw;
            }

            cancellationToken.ThrowIfCancellationRequested();
            var credentialChanges = connection.HasPendingCredentialChanges
                ? connection.GetPendingCredentialChanges()
                : new Dictionary<string, string>();
            DateTime completedAtUtc = DateTime.UtcNow;
            if (credentialChanges.Count > 0)
            {
                foreach (var (key, value) in credentialChanges)
                    credentials[key] = value;
                credentials.Remove("connectionId");
                string protectedCredentials = credentialVault.ProtectConnectionCredentials(
                    storageConnection, credentials);
                await storageConnections.UpdateRuntimeAsync(
                    storageConnection.Id,
                    protectedCredentials,
                    storageConnection.AccountDisplayName,
                    storageConnection.AccountEmail,
                    StorageConnectionState.Ready,
                    null,
                    cancellationToken);
                connection.AcknowledgeCredentialChanges();
            }

            await syncDefinitions.MarkCompletedAsync(
                definition.Id, completedAtUtc, cancellationToken);

            // Compatibility dual-write only. The first-class runtime and protected
            // connection are authoritative; a removed legacy mapping is not an
            // execution failure.
            await shares.UpdateCloudSyncRuntimeStateAsync(
                share.Id,
                normalizedPath,
                completedAtUtc,
                credentialChanges);
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

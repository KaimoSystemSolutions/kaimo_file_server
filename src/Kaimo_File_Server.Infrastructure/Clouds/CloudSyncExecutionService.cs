using System.Net;
using Kaimo_File_Server.Core.Domain;
using Kaimo_File_Server.Core.Domain.Identity;
using Kaimo_File_Server.Core.Repositories;
using Kaimo_File_Server.Core.Services;
using Kaimo_File_Server.Core.Services.DataServices;
using Kaimo_File_Server.Core.Services.File;
using Kaimo_File_Server.Core.Services.ExternalStorage;
using Kaimo_File_Server.Infrastructure.ExternalStorage;
using Kaimo_File_Server.Search;

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
    CompletedWithErrors,
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
    ICloudSyncOperationCoordinator operations,
    IStorageConnectionProviderCatalog? storageProviderCatalog = null,
    ISearchService? searchService = null) : ICloudSyncExecutionService
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
            if (storageConnection is null || storageConnection.State != StorageConnectionState.Ready)
                return CloudSyncExecutionResult.Missing;

            Dictionary<string, string> credentials = [];
            IStorageSession? providerSession = null;
            ICloudConnection connection;
            IStorageConnectionProvider? storageProvider = null;
            if (storageProviderCatalog is not null)
            {
                storageProvider = storageProviderCatalog.GetRequired(storageConnection.ProviderId);
                if (!storageProvider.Capabilities.HasFlag(StorageProviderCapabilities.Sync))
                    return CloudSyncExecutionResult.Missing;
            }
            if (storageProvider?.Capabilities.HasFlag(StorageProviderCapabilities.OptimizedSync) == true)
            {
                await RunOptimizedAsync(
                    storageProvider,
                    storageConnection,
                    share,
                    definition,
                    normalizedPath,
                    reportProgress,
                    cancellationToken);
                return CloudSyncExecutionResult.Completed;
            }
            if (storageProvider?.Capabilities.HasFlag(StorageProviderCapabilities.DirectFileAccess) == true
                || storageProvider?.Capabilities.HasFlag(StorageProviderCapabilities.RequiresHostMount) == true)
            {
                providerSession = await storageProvider.OpenSessionAsync(storageConnection, cancellationToken);
                var remoteFiles = providerSession.RemoteFiles
                                  ?? throw new NotSupportedException(
                                      "The storage provider does not expose the remote file contract required for synchronization.");
                connection = new RemoteFileStoreSyncAdapter(storageProvider.DisplayName, remoteFiles);
            }
            else
            {
                credentials = credentialVault.UnprotectConnectionCredentials(storageConnection);
                // The stable ID prevents refresh-token rotation from creating a new
                // live provider cache entry. Providers ignore this neutral key.
                credentials["connectionId"] = storageConnection.Id.ToString("D");
                var legacyFolder = new SyncedFolder(
                    storageConnection.ProviderId, credentials, definition.RemotePath);
                connection = providers.CreateOrLoad(share.Id, legacyFolder);
            }
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

            var fileService = fileServices.CreateForShare(share.Id, share.Path);

            // Two-way runs always refresh the converged-state manifest so enabling
            // delete propagation later takes effect on the next run. The baseline
            // is only loaded (to detect deletions) when the option is on.
            bool isTwoWay = definition.Mode == SyncMode.TwoWay;
            bool applyDeletions = isTwoWay && definition.AdvancedSettings.SyncDeletions;
            SyncManifest? previousManifest = applyDeletions
                ? SyncManifest.Deserialize(
                    await syncDefinitions.GetManifestAsync(definition.Id, cancellationToken))
                : null;
            SyncManifest? newManifest;
            var failures = new List<SyncFailure>();
            try
            {
                newManifest = await connection.SyncAsync(
                    fileService,
                    actor,
                    NormalizeRemotePath(folder.RemotePath),
                    normalizedPath,
                    folder.Mode,
                    // Local deletes route through the share recycle bin when it is
                    // enabled, keeping removed files recoverable.
                    new CloudSyncTransferOptions(folder.AdvancedSettings, share.IsRecycleEnabled),
                    previousManifest,
                    reportProgress,
                    failures,
                    cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                await syncDefinitions.MarkFailedAsync(
                    definition.Id,
                    DateTime.UtcNow,
                    ClassifyError(exception),
                    cancellationToken);
                if (exception is ProviderRequestException { Category: ProviderErrorCategory.Authentication } authError)
                    await MarkNeedsReauthorizationAsync(storageConnection.Id, authError.ErrorCode, cancellationToken);
                throw;
            }
            finally
            {
                if (providerSession is not null)
                    await providerSession.DisposeAsync();
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
                // Conditional on the version this run started from: a connection that
                // was disabled or re-authorized meanwhile must keep that newer state and
                // credential, and the connection state itself is never touched here.
                bool persisted = await storageConnections.TryUpdateCredentialAsync(
                    storageConnection.Id,
                    storageConnection.ConcurrencyVersion,
                    protectedCredentials,
                    protectedCredentials.StartsWith("dp:v2:", StringComparison.Ordinal)
                        ? StorageConnection.CurrentProtectorPurposeVersion
                        : 1,
                    cancellationToken);
                if (persisted)
                    connection.AcknowledgeCredentialChanges();
            }

            // A run that copied everything it could but hit individual-item
            // failures is completed (state and manifest are advanced), with the
            // failures recorded so the operator can see exactly what was skipped.
            await syncDefinitions.MarkCompletedAsync(
                definition.Id, completedAtUtc, failures, cancellationToken);

            // Persist the converged tree after any run that built one (two-way, for a
            // ready delete-propagation baseline; pull, for the browser's local-only
            // detection). newManifest is non-null exactly for those modes (see SyncAsync).
            if (newManifest is not null)
                await syncDefinitions.SaveManifestAsync(
                    definition.Id, newManifest.Serialize(), cancellationToken);

            // Compatibility dual-write only. The first-class runtime and protected
            // connection are authoritative; a removed legacy mapping is not an
            // execution failure.
            await shares.UpdateCloudSyncRuntimeStateAsync(
                share.Id,
                normalizedPath,
                completedAtUtc,
                credentialChanges);
            return failures.Count > 0
                ? CloudSyncExecutionResult.CompletedWithErrors
                : CloudSyncExecutionResult.Completed;
        }
    }

    /// <summary>
    /// A revoked or expired grant cannot heal by retrying. Parking the connection
    /// in <see cref="StorageConnectionState.NeedsReauthorization"/> stops the
    /// scheduler from hammering the provider (runs require <c>Ready</c>) and tells
    /// the operator what to do.
    /// </summary>
    private async Task MarkNeedsReauthorizationAsync(
        Guid connectionId, string errorCode, CancellationToken cancellationToken)
    {
        try
        {
            var current = await storageConnections.GetAsync(connectionId, cancellationToken);
            if (current is null || current.State != StorageConnectionState.Ready)
                return;
            current.State = StorageConnectionState.NeedsReauthorization;
            current.LastErrorCode = errorCode;
            await storageConnections.SaveAsync(current, cancellationToken);
        }
        catch (StorageConnectionConcurrencyException)
        {
            // Changed concurrently (e.g. just re-authorized); that state wins.
        }
    }

    private async Task RunOptimizedAsync(
        IStorageConnectionProvider provider,
        StorageConnection connection,
        ShareDefinition share,
        SyncDefinition definition,
        string normalizedLocalPath,
        Action<string?, int>? reportProgress,
        CancellationToken cancellationToken)
    {
        if (definition.Mode == SyncMode.TwoWay)
            throw new NotSupportedException(
                "Native rsync transports require an explicit pull or push direction.");

        var direction = definition.Mode == SyncMode.Pull
            ? OptimizedSyncDirection.Pull
            : OptimizedSyncDirection.Push;
        long? transferLimit = direction == OptimizedSyncDirection.Pull
            ? definition.AdvancedSettings.MaxDownloadBytesPerSecond
            : definition.AdvancedSettings.MaxUploadBytesPerSecond;
        var request = new OptimizedSyncRequest(
            share.Path,
            normalizedLocalPath,
            definition.RemotePath,
            direction,
            DeleteExtraneousFiles: false,
            definition.AdvancedSettings.ExcludedExtensions,
            definition.AdvancedSettings.MaxFileSizeBytes,
            transferLimit);

        IReadOnlyList<string> receivedFiles = [];
        IStorageSession? session = null;
        try
        {
            session = await provider.OpenSessionAsync(connection, cancellationToken);
            IOptimizedStorageSync optimizedSync = session.OptimizedSync
                ?? throw new NotSupportedException(
                    "The storage provider did not expose its advertised optimized synchronization contract.");
            reportProgress?.Invoke(null, 0);
            receivedFiles = await optimizedSync.SynchronizeAsync(request, cancellationToken) ?? [];
            reportProgress?.Invoke(null, 100);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            await syncDefinitions.MarkFailedAsync(
                definition.Id, DateTime.UtcNow, ClassifyError(exception), cancellationToken);
            throw;
        }
        finally
        {
            if (session is not null)
                await session.DisposeAsync();
        }

        DateTime completedAtUtc = DateTime.UtcNow;
        // The native transport reports success or throws as a whole; it has no
        // per-item failure list to record.
        await syncDefinitions.MarkCompletedAsync(
            definition.Id, completedAtUtc, cancellationToken: cancellationToken);
        await shares.UpdateCloudSyncRuntimeStateAsync(
            share.Id,
            normalizedLocalPath,
            completedAtUtc,
            new Dictionary<string, string>());

        // rsync writes straight to disk, bypassing FileService, so none of the
        // per-file search-index hooks fired. Feed exactly the files rsync reported
        // as received (from --itemize-changes) to the index — no full-share rescan.
        await IndexReceivedFilesAsync(receivedFiles, cancellationToken);
    }

    /// <summary>
    /// Indexes the files a pull just wrote to disk. Best-effort per file: the
    /// index is a rebuildable projection, so a disabled search backend or a single
    /// file that vanished between transfer and indexing must never fail a
    /// completed sync.
    /// </summary>
    private async Task IndexReceivedFilesAsync(
        IReadOnlyList<string> absolutePaths, CancellationToken cancellationToken)
    {
        if (searchService is null || absolutePaths.Count == 0)
            return;

        foreach (string absolutePath in absolutePaths)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Stream? content = null;
            try
            {
                content = new FileStream(
                    absolutePath, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, useAsync: true);
                // The search service takes ownership of the stream (as WriteFileAsync
                // and reindex do); clear our handle so it is not disposed twice.
                await searchService.onFileCreated(absolutePath, Task.FromResult<Stream>(content), cancellationToken);
                content = null;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch
            {
                // Skip this file; the manual reindex remains the catch-all repair.
            }
            finally
            {
                if (content is not null)
                    await content.DisposeAsync();
            }
        }
    }

    /// <summary>
    /// Maps a sync failure to a stable, explicit error code persisted as
    /// <c>LastErrorCode</c> and rendered as a localized message. It distinguishes
    /// the causes the operator most needs to tell apart — a removed local folder,
    /// a removed remote folder, a broken connection, and denied access — instead
    /// of collapsing everything into a single opaque "sync_failed".
    /// </summary>
    internal static string ClassifyError(Exception exception) => exception switch
    {
        SyncDirectoryMissingException { Endpoint: SyncEndpoint.Local } => "local_path_missing",
        SyncDirectoryMissingException => "remote_path_missing",
        DirectoryNotFoundException => "local_path_missing",
        RemoteStorageAccessDeniedException => "remote_access_denied",
        ProviderRequestException { StatusCode: HttpStatusCode.NotFound } => "remote_path_missing",
        ProviderRequestException providerError => providerError.ErrorCode,
        HttpRequestException => "connection_failed",
        System.Net.Sockets.SocketException => "connection_failed",
        TimeoutException => "connection_failed",
        IOException => "connection_failed",
        _ => "sync_failed"
    };

    private static string NormalizeRemotePath(string? path)
    {
        string normalized = string.IsNullOrWhiteSpace(path)
            ? ""
            : path.Replace('\\', '/').Trim('/');
        return normalized.Length == 0 ? "/" : $"/{normalized}";
    }
}

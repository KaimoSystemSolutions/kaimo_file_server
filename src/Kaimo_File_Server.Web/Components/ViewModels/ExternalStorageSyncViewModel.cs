using Kaimo_File_Server.Core.Domain;
using Kaimo_File_Server.Core.Domain.Identity;
using Kaimo_File_Server.Core.Helpers;
using Kaimo_File_Server.Core.Language;
using Kaimo_File_Server.Core.Repositories;
using Kaimo_File_Server.Core.Security;
using Kaimo_File_Server.Core.Services;
using Kaimo_File_Server.Core.Services.DataServices;
using Kaimo_File_Server.Core.Services.File;
using Kaimo_File_Server.Core.Services.ExternalStorage;
using Kaimo_File_Server.Infrastructure.Clouds;
using Microsoft.AspNetCore.Components.Authorization;

namespace Kaimo_File_Server.Web.Components.ViewModels;

/// <summary>
/// Administration boundary for Package 6 first-class sync definitions. The UI
/// never reads or writes provider credentials and all mutations are reauthorized
/// against the selected share and connection department.
/// </summary>
public sealed class ExternalStorageSyncViewModel(
    ISyncDefinitionRepository syncDefinitions,
    IStorageConnectionRepository connections,
    IShareRepository shares,
    ILegacyCloudSyncMigrationService legacyMigration,
    IManagementAuthService managementAuth,
    IUserContextFactory userContextFactory,
    AuthenticationStateProvider authenticationState,
    IFileServiceFactory fileServices,
    ICloudSyncJobRunner syncJobRunner,
    IStorageConnectionProviderCatalog providerCatalog,
    IStorageDirectoryTargetResolver directoryTargets,
    ICloudSyncOperationCoordinator syncOperations,
    CloudSyncSchedulerSignal schedulerSignal,
    ILogger<ExternalStorageSyncViewModel> logger)
{
    private UserContext? _actor;

    public IReadOnlyList<ExternalStorageSyncListItem> Syncs { get; private set; } = [];
    public IReadOnlyList<ShareDefinition> Shares { get; private set; } = [];
    public IReadOnlyList<StorageConnection> Connections { get; private set; } = [];
    public bool IsLoading { get; private set; }
    public bool IsBusy { get; private set; }
    public string? ErrorMessage { get; private set; }

    public async Task LoadAsync()
    {
        IsLoading = true;
        ErrorMessage = null;
        try
        {
            await legacyMigration.EnsureMigratedAsync();
            _actor = await GetActorAsync();
            if (_actor is null)
            {
                Syncs = [];
                Shares = [];
                Connections = [];
                return;
            }

            var allShares = await shares.GetAllAsync();
            var shareScope = await managementAuth.GetAuthorizedShareIdsAnyAsync(
                _actor, ManagementPermission.SyncAdmin);
            var visibleShareIds = shareScope.IsUnrestricted
                ? allShares.Select(share => share.Id).ToHashSet()
                : shareScope.ScopeIds.ToHashSet();
            Shares = allShares.Where(share => visibleShareIds.Contains(share.Id))
                .OrderBy(share => share.Name, StringComparer.CurrentCultureIgnoreCase)
                .ToArray();

            var allConnections = await connections.GetAllAsync();
            // Connections are global: visible to any holder of global UseConnections.
            Connections = await managementAuth.HasGlobalPermissionAsync(
                    _actor, ManagementPermission.UseConnections)
                ? allConnections
                    .OrderBy(connection => connection.Name, StringComparer.CurrentCultureIgnoreCase)
                    .ToArray()
                : [];

            var shareNames = allShares.ToDictionary(share => share.Id, share => share.Name);
            var connectionNames = allConnections.ToDictionary(connection => connection.Id, connection => connection.Name);
            Syncs = (await syncDefinitions.GetAllAsync())
                .Where(entry => visibleShareIds.Contains(entry.Definition.LocalShareId))
                .Where(entry => entry.Definition.MigrationSource != SyncDefinition.DeletedByFirstClassEditorSource)
                .Select(entry => new ExternalStorageSyncListItem(
                    entry.Definition,
                    entry.Runtime,
                    shareNames.GetValueOrDefault(entry.Definition.LocalShareId, "—"),
                    connectionNames.GetValueOrDefault(entry.Definition.ConnectionId, "—")))
                .OrderBy(item => item.ShareName, StringComparer.CurrentCultureIgnoreCase)
                .ThenBy(item => item.Definition.LocalPath, StringComparer.CurrentCultureIgnoreCase)
                .ToArray();
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Unable to load first-class external-storage sync administration");
            ErrorMessage = Text("Web_ExternalStorage_LoadFailed", "External storage could not be loaded.");
        }
        finally
        {
            IsLoading = false;
        }
    }

    public async Task CreateAsync(ExternalStorageSyncEditModel model)
    {
        ArgumentNullException.ThrowIfNull(model);
        await EnsureActorAsync();
        var share = await GetAuthorizedShareAsync(model.LocalShareId, ManagementPermission.CreateSyncs);
        var connection = await GetUsableConnectionAsync(model.ConnectionId);
        EnsureCapability(connection, StorageProviderCapabilities.Sync, "Web_ExternalStorage_SyncUnsupported",
            "This provider does not support synchronization.");
        EnsureDirectionSupported(connection, model.Mode);
        if (providerCatalog.GetRequired(connection.ProviderId).Capabilities
            .HasFlag(StorageProviderCapabilities.Browse))
            model.RemotePath = (await directoryTargets.ResolveDirectoryAsync(connection, model.RemotePath)).Path;
        await ValidateModelAsync(model, share, existingId: null);

        string normalizedLocalPath = ShareRelativePath.Normalize(model.LocalPath);
        var deletedImport = await syncDefinitions.GetBySharePathAsync(share.Id, normalizedLocalPath);
        var definition = deletedImport?.MigrationSource == SyncDefinition.DeletedByFirstClassEditorSource
            ? deletedImport
            : new SyncDefinition();
        definition.ConnectionId = connection.Id;
        definition.LocalShareId = share.Id;
        definition.LocalPath = normalizedLocalPath;
        definition.RemotePath = NormalizeRemotePath(model.RemotePath);
        definition.Mode = model.Mode;
        definition.Schedule = BuildSchedule(model);
        definition.AdvancedSettings = BuildAdvancedSettings(model);
        definition.DisplayName = model.DisplayName.Trim();
        definition.Description = model.Description.Trim();
        definition.Enabled = model.Enabled;
        definition.RunAsUserId = model.ScheduleEnabled ? _actor!.User.Id : null;
        definition.CreatedByUserId ??= _actor!.User.Id;
        await syncDefinitions.SaveAsync(definition);
        schedulerSignal.Wake();
        await LoadAsync();
    }

    public async Task UpdateAsync(Guid id, ExternalStorageSyncEditModel model)
    {
        ArgumentNullException.ThrowIfNull(model);
        await EnsureActorAsync();
        var definition = await syncDefinitions.GetAsync(id)
                         ?? throw new InvalidOperationException(Text(
                             "Web_ExternalStorage_SyncMissing", "The sync no longer exists."));
        var share = await GetAuthorizedShareAsync(definition.LocalShareId, ManagementPermission.ConfigureSyncs);
        var connection = await GetUsableConnectionAsync(model.ConnectionId);
        EnsureCapability(connection, StorageProviderCapabilities.Sync, "Web_ExternalStorage_SyncUnsupported",
            "This provider does not support synchronization.");
        EnsureDirectionSupported(connection, model.Mode);
        if (providerCatalog.GetRequired(connection.ProviderId).Capabilities
            .HasFlag(StorageProviderCapabilities.Browse))
            model.RemotePath = (await directoryTargets.ResolveDirectoryAsync(connection, model.RemotePath)).Path;
        await ValidateModelAsync(model, share, definition.Id);

        string newLocalPath = ShareRelativePath.Normalize(model.LocalPath);
        var mutationLease = await syncOperations.TryBeginPathMutationAsync(
            definition.LocalShareId, definition.LocalPath, newLocalPath);
        if (mutationLease is null)
            throw new InvalidOperationException(Text(
                "Web_CloudSync_Error_Busy", "This folder is already being synchronized or modified."));
        await using var mutation = mutationLease;

        definition.ConnectionId = connection.Id;
        definition.LocalPath = newLocalPath;
        definition.RemotePath = NormalizeRemotePath(model.RemotePath);
        definition.Mode = model.Mode;
        definition.Schedule = BuildSchedule(model);
        definition.AdvancedSettings = BuildAdvancedSettings(model);
        definition.DisplayName = model.DisplayName.Trim();
        definition.Description = model.Description.Trim();
        definition.Enabled = model.Enabled;
        definition.RunAsUserId = model.ScheduleEnabled ? _actor!.User.Id : definition.RunAsUserId;
        await syncDefinitions.SaveAsync(definition);
        schedulerSignal.Wake();
        await LoadAsync();
    }

    public async Task DeleteAsync(Guid id)
    {
        await EnsureActorAsync();
        var definition = await syncDefinitions.GetAsync(id)
                         ?? throw new InvalidOperationException(Text(
                             "Web_ExternalStorage_SyncMissing", "The sync no longer exists."));
        await GetAuthorizedShareAsync(definition.LocalShareId, ManagementPermission.DeleteSyncs);
        var mutationLease = await syncOperations.TryBeginPathMutationAsync(
            definition.LocalShareId, definition.LocalPath, definition.LocalPath);
        if (mutationLease is null)
            throw new InvalidOperationException(Text(
                "Web_CloudSync_Error_Busy", "This folder is already being synchronized or modified."));
        await using var mutation = mutationLease;
        await syncDefinitions.DeleteAsync(id);
        schedulerSignal.Wake();
        await LoadAsync();
    }

    /// <summary>
    /// Queues a manual sync as a background job and returns immediately. The sync
    /// runs off the UI circuit (so a long or hanging sync never freezes the web
    /// UI) and shows up in the Running Jobs menu; the caller reloads the list when
    /// the job reports completion via <see cref="ICloudSyncJobRunner.OnChanged"/>.
    /// </summary>
    public async Task<Guid> RunAsync(Guid id)
    {
        await EnsureActorAsync();
        var definition = await syncDefinitions.GetAsync(id)
                         ?? throw new InvalidOperationException(Text(
                             "Web_ExternalStorage_SyncMissing", "The sync no longer exists."));
        await GetAuthorizedShareAsync(definition.LocalShareId, ManagementPermission.SyncManually);

        string title = string.Format(
            Text("Web_Jobs_CloudSync_Title", "Cloud sync: {0}"),
            string.IsNullOrWhiteSpace(definition.DisplayName)
                ? definition.LocalPath
                : definition.DisplayName);
        return syncJobRunner.Enqueue(
            definition.LocalShareId,
            definition.LocalPath,
            _actor!.User.Id,
            title,
            $"{definition.LocalPath} ↔ {definition.RemotePath}");
    }

    /// <summary>
    /// Lists remote directories through the provider-neutral sync contract. If
    /// the provider rotates credentials while browsing, the updated grant is
    /// protected and persisted before the provider change is acknowledged.
    /// </summary>
    public async Task<IReadOnlyList<CloudDirectoryItem>> ListRemoteDirectoriesAsync(
        Guid connectionId,
        Guid localShareId,
        string path)
    {
        await EnsureActorAsync();
        var storageConnection = await GetUsableConnectionAsync(connectionId);
        // A local share is optional while browsing: authorization is already
        // enforced on the connection (global UseConnections). When one is chosen we
        // still verify the actor may configure syncs on it.
        if (localShareId != Guid.Empty)
            await GetBrowsableShareAsync(localShareId);

        try
        {
            var items = await directoryTargets.ListDirectoriesAsync(storageConnection, path);
            return items
                .Select(item => new CloudDirectoryItem(item.Name, NormalizeRemotePath(item.Path)))
                .ToArray();
        }
        catch (Exception exception)
        {
            // The picker shows a localized generic message, so the sanitized
            // provider cause is logged here to keep the failure diagnosable.
            logger.LogWarning(
                exception,
                "Remote directory browse failed for connection {ConnectionId} at path {RemotePath}",
                connectionId, path);
            throw;
        }
    }

    /// <summary>
    /// Lists local child directories inside the selected share through the
    /// share-scoped file service so ACL and path-containment rules stay enforced
    /// while an administrator picks the folder to synchronize.
    /// </summary>
    public async Task<IReadOnlyList<CloudDirectoryItem>> ListLocalDirectoriesAsync(
        Guid localShareId,
        string path)
    {
        await EnsureActorAsync();
        var share = await GetBrowsableShareAsync(localShareId);
        string normalized = ShareRelativePath.Normalize(path);
        var items = await fileServices.CreateForShare(share.Id, share.Path)
            .ListAsync(normalized, _actor!);
        return items
            .Where(item => item.IsDirectory)
            .OrderBy(item => item.Name, StringComparer.CurrentCultureIgnoreCase)
            .Select(item => new CloudDirectoryItem(
                item.Name,
                normalized.Length == 0 ? item.Name : $"{normalized}/{item.Name}"))
            .ToArray();
    }

    public StorageProviderCapabilities GetCapabilities(StorageConnection connection)
        => providerCatalog.TryGet(connection.ProviderId, out var provider)
            ? provider.Capabilities
            : StorageProviderCapabilities.None;

    public bool Supports(StorageConnection connection, StorageProviderCapabilities capability)
        => GetCapabilities(connection).HasFlag(capability);

    public string GetProviderName(string providerId)
        => providerCatalog.TryGet(providerId, out var provider) ? provider.DisplayName : providerId;

    private async Task ValidateModelAsync(
        ExternalStorageSyncEditModel model,
        ShareDefinition share,
        Guid? existingId)
    {
        if (string.IsNullOrWhiteSpace(model.DisplayName) || model.DisplayName.Trim().Length > 200)
            throw new ArgumentException(Text("Web_ExternalStorage_InvalidName", "Enter a name of at most 200 characters."));
        if (model.Description.Length > 2000)
            throw new ArgumentException(Text("Web_ExternalStorage_InvalidDescription", "The description is too long."));
        if (model.IntervalSeconds is < CloudSyncSchedule.MinIntervalSeconds or > CloudSyncSchedule.MaxIntervalSeconds)
            throw new ArgumentException(Text("Web_CloudSync_Schedule_Error_Interval", "Enter an interval between 1 and 86400 seconds."));
        if (model.ScheduleEnabled && !model.ActiveScheduleSlots.Any(CloudSyncSchedule.IsValidSlot))
            throw new ArgumentException(Text("Web_CloudSync_Schedule_Error_Empty", "Select at least one hour before enabling the timer."));
        if (model.MaxFileSizeMb is < 0 || model.MaxUploadRateKbps is < 0 || model.MaxDownloadRateKbps is < 0)
            throw new ArgumentException(Text("Web_CloudSync_Advanced_Error_Negative", "Advanced limits cannot be negative."));

        string localPath = ShareRelativePath.Normalize(model.LocalPath);
        var conflict = (await syncDefinitions.GetAllAsync())
            .Select(entry => entry.Definition)
            .FirstOrDefault(candidate => candidate.LocalShareId == share.Id
                && candidate.Id != existingId
                && candidate.MigrationSource != SyncDefinition.DeletedByFirstClassEditorSource
                && (CloudSyncPaths.IsSameOrAncestor(candidate.LocalPath, localPath)
                    || CloudSyncPaths.IsSameOrAncestor(localPath, candidate.LocalPath)));
        if (conflict is not null)
            throw new InvalidOperationException(Text(
                "Web_ExternalStorage_PathInUse", "This local folder already has a sync."));

        var metadata = await fileServices.CreateForShare(share.Id, share.Path)
            .GetMetadataAsync(localPath, _actor!);
        if (!metadata.IsDirectory)
            throw new InvalidOperationException(Text(
                "Web_CloudSync_Error_LocalFolder", "Select an existing local folder."));
    }

    private async Task<ShareDefinition> GetAuthorizedShareAsync(Guid id, ManagementPermission permission)
    {
        var share = await shares.GetByIdAsync(id)
                    ?? throw new InvalidOperationException(Text(
                        "Web_ExternalStorage_ShareMissing", "The local share no longer exists."));
        if (!await managementAuth.CanManageShareAsync(_actor!, id, permission))
            throw new UnauthorizedAccessException(Text(
                "Web_CloudSync_Error_NotAuthorized", "You are not authorized for this action."));
        return share;
    }

    private async Task<ShareDefinition> GetBrowsableShareAsync(Guid id)
    {
        var share = await shares.GetByIdAsync(id)
                    ?? throw new InvalidOperationException(Text(
                        "Web_ExternalStorage_ShareMissing", "The local share no longer exists."));
        bool canCreate = await managementAuth.CanManageShareAsync(
            _actor!, id, ManagementPermission.CreateSyncs);
        bool canConfigure = await managementAuth.CanManageShareAsync(
            _actor!, id, ManagementPermission.ConfigureSyncs);
        if (!canCreate && !canConfigure)
            throw new UnauthorizedAccessException(Text(
                "Web_CloudSync_Error_NotAuthorized", "You are not authorized for this action."));
        return share;
    }

    private async Task<StorageConnection> GetUsableConnectionAsync(Guid id)
    {
        var connection = await connections.GetAsync(id)
                         ?? throw new InvalidOperationException(Text(
                             "Web_CloudAccess_ConnectionMissing", "The connection no longer exists."));
        if (!await managementAuth.HasGlobalPermissionAsync(
                _actor!, ManagementPermission.UseConnections))
            throw new UnauthorizedAccessException(Text(
                "Web_StorageConnection_UseDenied", "You may not use connections."));
        if (connection.State != StorageConnectionState.Ready
            || (RequiresProtectedCredential(connection.AuthorizationMode)
                && string.IsNullOrWhiteSpace(connection.EncryptedCredentialPayload)))
            throw new InvalidOperationException(Text(
                "Web_CloudAccess_ConnectionNotReady", "The connection is not ready."));
        return connection;
    }

    private void EnsureCapability(
        StorageConnection connection,
        StorageProviderCapabilities capability,
        string resourceKey,
        string fallback)
    {
        var provider = providerCatalog.GetRequired(connection.ProviderId);
        if (!provider.Capabilities.HasFlag(capability))
            throw new NotSupportedException(Text(resourceKey, fallback));
    }

    private void EnsureDirectionSupported(StorageConnection connection, SyncMode mode)
    {
        StorageProviderCapabilities capabilities = providerCatalog.GetRequired(connection.ProviderId).Capabilities;
        if (mode == SyncMode.TwoWay
            && capabilities.HasFlag(StorageProviderCapabilities.OptimizedSync))
            throw new NotSupportedException(Text(
                "Web_ExternalStorage_RsyncDirectionRequired",
                "Rsync connections require an explicit pull or push direction."));
        if (mode != SyncMode.Pull && !capabilities.HasFlag(StorageProviderCapabilities.Write))
            throw new NotSupportedException(Text(
                "Web_ExternalStorage_ReadOnlyPullRequired",
                "This connection supports pull synchronization only."));
    }

    private static bool RequiresProtectedCredential(StorageAuthorizationMode mode)
        => mode is not (StorageAuthorizationMode.HostMount
            or StorageAuthorizationMode.SshKey
            or StorageAuthorizationMode.NetworkIdentity);

    private async Task EnsureActorAsync()
    {
        _actor ??= await GetActorAsync();
        if (_actor is null)
            throw new UnauthorizedAccessException(Text(
                "Web_CloudSync_Error_NotAuthorized", "You are not authorized for this action."));
    }

    private async Task<UserContext?> GetActorAsync()
    {
        var state = await authenticationState.GetAuthenticationStateAsync();
        string? username = state.User.Identity?.Name;
        return string.IsNullOrWhiteSpace(username)
            ? null
            : await userContextFactory.CreateByUsernameAsync(username);
    }

    private static CloudSyncSchedule BuildSchedule(ExternalStorageSyncEditModel model) => new()
    {
        IsEnabled = model.ScheduleEnabled,
        IntervalSeconds = model.IntervalSeconds,
        ActiveSlots = new HashSet<int>(model.ActiveScheduleSlots.Where(CloudSyncSchedule.IsValidSlot))
    };

    private static CloudSyncAdvancedSettings BuildAdvancedSettings(ExternalStorageSyncEditModel model) => new()
    {
        MaxFileSizeBytes = ToBytes(model.MaxFileSizeMb, 1024L * 1024),
        MaxUploadBytesPerSecond = ToBytes(model.MaxUploadRateKbps, 1024L),
        MaxDownloadBytesPerSecond = ToBytes(model.MaxDownloadRateKbps, 1024L),
        ExcludedExtensions = (model.ExcludedExtensions ?? string.Empty)
            .Split([',', ';', ' ', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(extension => extension.StartsWith('.') ? extension : $".{extension}")
            .ToHashSet(StringComparer.OrdinalIgnoreCase),
        SyncDeletions = model.SyncDeletions
    };

    private static long? ToBytes(long? value, long multiplier)
        => value is > 0 ? checked(value.Value * multiplier) : null;

    private static string NormalizeRemotePath(string? path)
    {
        string normalized = string.IsNullOrWhiteSpace(path) ? string.Empty : path.Replace('\\', '/').Trim('/');
        return normalized.Length == 0 ? "/" : $"/{normalized}";
    }

    private static string Text(string key, string fallback)
        => Resources.ResourceManager.GetString(key) ?? fallback;
}

public sealed record ExternalStorageSyncListItem(
    SyncDefinition Definition,
    SyncDefinitionRuntime? Runtime,
    string ShareName,
    string ConnectionName);

public sealed class ExternalStorageSyncEditModel
{
    public Guid ConnectionId { get; set; }
    public Guid LocalShareId { get; set; }
    public string DisplayName { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string LocalPath { get; set; } = string.Empty;
    public string RemotePath { get; set; } = "/";
    public SyncMode Mode { get; set; } = SyncMode.TwoWay;
    public bool Enabled { get; set; } = true;
    public bool ScheduleEnabled { get; set; }
    public int IntervalSeconds { get; set; } = 3600;
    public HashSet<int> ActiveScheduleSlots { get; set; } = [];
    public long? MaxFileSizeMb { get; set; }
    public long? MaxUploadRateKbps { get; set; }
    public long? MaxDownloadRateKbps { get; set; }
    public string ExcludedExtensions { get; set; } = string.Empty;

    /// <summary>Propagate deletions in two-way mode instead of restoring them.</summary>
    public bool SyncDeletions { get; set; }
}

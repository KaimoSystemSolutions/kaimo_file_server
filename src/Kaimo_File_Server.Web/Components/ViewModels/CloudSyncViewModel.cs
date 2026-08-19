using Kaimo_File_Server.Core.Domain;
using Kaimo_File_Server.Core.Domain.Identity;
using Kaimo_File_Server.Core.Language;
using Kaimo_File_Server.Core.Repositories;
using Kaimo_File_Server.Core.Security;
using Kaimo_File_Server.Core.Services;
using Kaimo_File_Server.Core.Services.DataServices;
using Kaimo_File_Server.Core.Services.File;
using Kaimo_File_Server.Infrastructure.Clouds;
using Kaimo_File_Server.Web.Services;
using Microsoft.AspNetCore.Components.Authorization;

namespace Kaimo_File_Server.Web.Components.ViewModels;

/// <summary>
/// Provider-neutral state and operations for the cloud-sync administration page.
/// Razor components only render this state; authorization and persistence are
/// re-checked here for every mutating operation.
/// </summary>
public sealed class CloudSyncViewModel
{
    private readonly IShareRepository _shareRepository;
    private readonly ICloudProviderFactory _providerFactory;
    private readonly IFileServiceFactory _fileServiceFactory;
    private readonly ICloudSyncExecutionService _syncExecution;
    private readonly ICloudSyncOperationCoordinator _syncOperations;
    private readonly IManagementAuthService _managementAuth;
    private readonly IUserContextFactory _userContextFactory;
    private readonly AuthenticationStateProvider _authenticationState;
    private readonly ICloudAuthorizationTicketStore _authorizationTickets;
    private readonly CloudSyncSchedulerSignal _schedulerSignal;
    private readonly ILogger<CloudSyncViewModel> _logger;
    private readonly Dictionary<Guid, ManagementPermission> _permissions = [];
    private UserContext? _actor;
    private bool _remoteFolderSelectedExplicitly;

    /// <summary>Creates the provider-neutral administration model and its security dependencies.</summary>
    public CloudSyncViewModel(
        IShareRepository shareRepository,
        ICloudProviderFactory providerFactory,
        IFileServiceFactory fileServiceFactory,
        ICloudSyncExecutionService syncExecution,
        ICloudSyncOperationCoordinator syncOperations,
        IManagementAuthService managementAuth,
        IUserContextFactory userContextFactory,
        AuthenticationStateProvider authenticationState,
        ICloudAuthorizationTicketStore authorizationTickets,
        CloudSyncSchedulerSignal schedulerSignal,
        ILogger<CloudSyncViewModel> logger)
    {
        _shareRepository = shareRepository;
        _providerFactory = providerFactory;
        _fileServiceFactory = fileServiceFactory;
        _syncExecution = syncExecution;
        _syncOperations = syncOperations;
        _managementAuth = managementAuth;
        _userContextFactory = userContextFactory;
        _authenticationState = authenticationState;
        _authorizationTickets = authorizationTickets;
        _schedulerSignal = schedulerSignal;
        _logger = logger;
    }

    public IReadOnlyList<ShareDefinition> Shares { get; private set; } = [];
    public IReadOnlyList<CloudSyncListItem> Syncs { get; private set; } = [];
    public IReadOnlyList<ICloudProvider> Providers { get; private set; } = [];
    public CloudSyncListItem? SelectedSync { get; private set; }
    public CloudAccountInfo? SelectedAccount { get; private set; }

    public bool IsLoading { get; private set; }
    public bool IsBusy { get; private set; }

    /// <summary>
    /// Distinguishes an expected user cancellation from a provider failure so
    /// the page can show a neutral result instead of an error notification.
    /// </summary>
    public bool LastSyncWasCancelled { get; private set; }
    public bool IsCreating { get; set; }
    public string? ErrorMessage { get; private set; }

    public Guid NewShareId { get; set; }
    public string NewProviderId { get; set; } = "";
    public string NewLocalPath { get; set; } = "";

    public string EditLocalPath { get; set; } = "";
    public string EditRemotePath { get; set; } = "/";
    public string EditDisplayName { get; set; } = "";
    public string EditDescription { get; set; } = "";
    public SyncMode EditMode { get; set; } = SyncMode.TwoWay;
    public long? EditMaxFileSizeMb { get; set; }
    public string EditExcludedExtensions { get; set; } = "";
    public long? EditMaxUploadRateKbps { get; set; }
    public long? EditMaxDownloadRateKbps { get; set; }

    /// <summary>Propagate deletions in two-way mode instead of restoring them.</summary>
    public bool EditSyncDeletions { get; set; }
    public bool EditScheduleEnabled { get; set; }
    public int EditScheduleIntervalSeconds { get; set; } = CloudSyncSchedule.DefaultIntervalSeconds;
    public HashSet<int> EditScheduleSlots { get; private set; } = [];

    public int ActiveScheduleSlotCount => EditScheduleSlots.Count;

    /// <summary>
    /// Gets whether a newly authorized mapping still requires the administrator
    /// to explicitly confirm its remote folder. The remote root is valid, but
    /// must be selected intentionally rather than accepted as an implicit default.
    /// </summary>
    public bool RequiresRemoteFolderSelection
        => SelectedSync?.Configuration.RequiresRemoteFolderSelection == true;

    /// <summary>
    /// Gets whether the selected mapping has edits that differ from its persisted
    /// configuration, including a pending explicit remote-folder confirmation.
    /// </summary>
    public bool HasUnsavedChanges
    {
        get
        {
            if (SelectedSync is null)
                return false;

            var configuration = SelectedSync.Configuration;
            var schedule = configuration.Schedule ?? new CloudSyncSchedule();
            return (RequiresRemoteFolderSelection && _remoteFolderSelectedExplicitly)
                   || !string.Equals(
                       CloudSyncPaths.Normalize(EditLocalPath),
                       SelectedSync.LocalPath,
                       StringComparison.OrdinalIgnoreCase)
                   || !string.Equals(
                       NormalizeRemotePath(EditRemotePath),
                       NormalizeRemotePath(configuration.RemotePath),
                       StringComparison.Ordinal)
                   || EditMode != configuration.Mode
                   || !string.Equals(EditDisplayName, configuration.DisplayName, StringComparison.Ordinal)
                   || !string.Equals(EditDescription, configuration.Description, StringComparison.Ordinal)
                   || EditMaxFileSizeMb != ToMegabytes(configuration.AdvancedSettings?.MaxFileSizeBytes)
                   || EditMaxUploadRateKbps != ToKilobytes(configuration.AdvancedSettings?.MaxUploadBytesPerSecond)
                   || EditMaxDownloadRateKbps != ToKilobytes(configuration.AdvancedSettings?.MaxDownloadBytesPerSecond)
                   || !ParseExtensions(EditExcludedExtensions).SetEquals(configuration.AdvancedSettings?.ExcludedExtensions ?? [])
                   || EditSyncDeletions != (configuration.AdvancedSettings?.SyncDeletions ?? false)
                   || EditScheduleEnabled != schedule.IsEnabled
                   || EditScheduleIntervalSeconds != schedule.GetEffectiveIntervalSeconds()
                   || !EditScheduleSlots.SetEquals(schedule.ActiveSlots.Where(CloudSyncSchedule.IsValidSlot));
        }
    }

    public bool CanCreateSync => Shares.Any(share => HasPermission(share.Id, ManagementPermission.CreateSyncs));
    public bool CanConfigureSelected => SelectedSync is not null
        && HasPermission(SelectedSync.ShareId, ManagementPermission.ConfigureSyncs);
    public bool CanDeleteSelected => SelectedSync is not null
        && HasPermission(SelectedSync.ShareId, ManagementPermission.DeleteSyncs);
    public bool CanRunSelected => SelectedSync is not null
        && HasPermission(SelectedSync.ShareId, ManagementPermission.SyncManually);

    /// <summary>Checks the already-loaded effective permission for one share.</summary>
    public bool CanCreateOnShare(Guid shareId)
        => HasPermission(shareId, ManagementPermission.CreateSyncs);

    /// <summary>
    /// Loads only shares visible to the current actor, computes per-share
    /// permissions, and rebuilds the provider-neutral sync inventory.
    /// </summary>
    public async Task LoadAsync(Guid? preferredShareId = null, string? preferredLocalPath = null)
    {
        IsLoading = true;
        ErrorMessage = null;

        try
        {
            var state = await _authenticationState.GetAuthenticationStateAsync();
            var username = state.User.Identity?.Name;
            _actor = string.IsNullOrWhiteSpace(username)
                ? null
                : await _userContextFactory.CreateByUsernameAsync(username);

            if (_actor is null)
            {
                Shares = [];
                Syncs = [];
                return;
            }

            var scope = await _managementAuth.GetAuthorizedShareIdsAnyAsync(
                _actor, ManagementPermission.SyncAdmin);
            var allowedIds = scope.ScopeIds.ToHashSet();
            var allShares = await _shareRepository.GetAllAsync();
            Shares = allShares
                .Where(share => scope.IsUnrestricted || allowedIds.Contains(share.Id))
                .OrderBy(share => share.Name, StringComparer.CurrentCultureIgnoreCase)
                .ToArray();

            _permissions.Clear();
            foreach (var share in Shares)
            {
                _permissions[share.Id] = await _managementAuth.GetEffectivePermissionsAtAsync(
                    _actor, ScopeType.Share, share.Id);
            }

            Providers = _providerFactory.Providers
                .OrderBy(provider => provider.DisplayName, StringComparer.CurrentCultureIgnoreCase)
                .ToArray();
            RebuildSyncList();

            NewShareId = Shares.FirstOrDefault(share =>
                    HasPermission(share.Id, ManagementPermission.CreateSyncs))?.Id
                ?? Guid.Empty;
            NewProviderId = Providers.FirstOrDefault()?.Id ?? "";

            CloudSyncListItem? preferred = null;
            if (preferredShareId is not null)
            {
                var normalized = CloudSyncPaths.Normalize(preferredLocalPath);
                preferred = Syncs.FirstOrDefault(item =>
                    item.ShareId == preferredShareId
                    && (preferredLocalPath is null
                        || string.Equals(item.LocalPath, normalized, StringComparison.OrdinalIgnoreCase)));
            }

            if (preferred is not null)
                await SelectAsync(preferred);
            else if (SelectedSync is not null)
                await SelectMatchingAsync(SelectedSync.ShareId, SelectedSync.LocalPath);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unable to load cloud-sync administration data");
            ErrorMessage = Text("Web_CloudSync_Error_Load", "The cloud syncs could not be loaded.");
            Shares = [];
            Syncs = [];
        }
        finally
        {
            IsLoading = false;
        }
    }

    /// <summary>
    /// Selects a mapping for editing and loads optional account metadata without
    /// allowing a stale cloud credential to hide the rest of the inventory.
    /// </summary>
    public async Task SelectAsync(CloudSyncListItem item)
    {
        SelectedSync = item;
        EditLocalPath = item.LocalPath;
        EditRemotePath = NormalizeRemotePath(item.Configuration.RemotePath);
        EditDisplayName = item.Configuration.DisplayName;
        EditDescription = item.Configuration.Description;
        EditMode = item.Configuration.Mode;
        EditMaxFileSizeMb = ToMegabytes(item.Configuration.AdvancedSettings?.MaxFileSizeBytes);
        EditExcludedExtensions = string.Join(", ", item.Configuration.AdvancedSettings?.ExcludedExtensions?.Order(StringComparer.OrdinalIgnoreCase) ?? Enumerable.Empty<string>());
        EditMaxUploadRateKbps = ToKilobytes(item.Configuration.AdvancedSettings?.MaxUploadBytesPerSecond);
        EditMaxDownloadRateKbps = ToKilobytes(item.Configuration.AdvancedSettings?.MaxDownloadBytesPerSecond);
        EditSyncDeletions = item.Configuration.AdvancedSettings?.SyncDeletions ?? false;
        EditScheduleEnabled = item.Configuration.Schedule?.IsEnabled == true;
        EditScheduleIntervalSeconds = item.Configuration.Schedule?.GetEffectiveIntervalSeconds()
            ?? CloudSyncSchedule.DefaultIntervalSeconds;
        EditScheduleSlots = item.Configuration.Schedule?.ActiveSlots is { } slots
            ? new HashSet<int>(slots.Where(CloudSyncSchedule.IsValidSlot))
            : [];
        _remoteFolderSelectedExplicitly = false;
        SelectedAccount = null;
        ErrorMessage = null;

        try
        {
            var connection = _providerFactory.CreateOrLoad(item.ShareId, item.Configuration);
            SelectedAccount = await connection.GetAccountInfoAsync();
            await PersistPendingCredentialsAsync(item.ShareId, item.LocalPath, connection);
        }
        catch (Exception ex)
        {
            // A stale credential must not make the full inventory unavailable.
            _logger.LogWarning(ex, "Unable to load cloud account metadata for share {ShareId}", item.ShareId);
        }
    }

    /// <summary>Clears editing state and provider account metadata.</summary>
    public void Deselect()
    {
        SelectedSync = null;
        SelectedAccount = null;
        EditScheduleEnabled = false;
        EditScheduleIntervalSeconds = CloudSyncSchedule.DefaultIntervalSeconds;
        EditScheduleSlots = [];
        EditDisplayName = "";
        EditDescription = "";
        EditMaxFileSizeMb = null;
        EditExcludedExtensions = "";
        EditMaxUploadRateKbps = null;
        EditMaxDownloadRateKbps = null;
        EditSyncDeletions = false;
        _remoteFolderSelectedExplicitly = false;
        ErrorMessage = null;
    }

    public bool IsScheduleSlotActive(DayOfWeek day, int hour)
        => EditScheduleSlots.Contains(CloudSyncSchedule.ToSlot(day, hour));

    public void ToggleScheduleSlot(DayOfWeek day, int hour)
    {
        int slot = CloudSyncSchedule.ToSlot(day, hour);
        if (!EditScheduleSlots.Add(slot))
            EditScheduleSlots.Remove(slot);
    }

    public void SetScheduleSlot(DayOfWeek day, int hour, bool active)
        => SetScheduleSlotCore(day, hour, active);

    public void SetScheduleDay(DayOfWeek day, bool active)
    {
        foreach (int hour in Enumerable.Range(0, CloudSyncSchedule.HoursPerDay))
            SetScheduleSlotCore(day, hour, active);
    }

    public void SetScheduleHour(int hour, bool active)
    {
        foreach (DayOfWeek day in Enum.GetValues<DayOfWeek>())
            SetScheduleSlotCore(day, hour, active);
    }

    public void ToggleScheduleDay(DayOfWeek day)
    {
        bool activate = Enumerable.Range(0, CloudSyncSchedule.HoursPerDay)
            .Any(hour => !IsScheduleSlotActive(day, hour));
        foreach (int hour in Enumerable.Range(0, CloudSyncSchedule.HoursPerDay))
            SetScheduleSlotCore(day, hour, activate);
    }

    public void ToggleScheduleHour(int hour)
    {
        DayOfWeek[] days = Enum.GetValues<DayOfWeek>();
        bool activate = days.Any(day => !IsScheduleSlotActive(day, hour));
        foreach (DayOfWeek day in days)
            SetScheduleSlotCore(day, hour, activate);
    }

    public void ClearSchedule() => EditScheduleSlots.Clear();

    /// <summary>
    /// Records an intentional choice in the remote-folder picker. This also
    /// allows an administrator to explicitly keep the provider root.
    /// </summary>
    public void ConfirmRemoteFolderSelection() => _remoteFolderSelectedExplicitly = true;

    private void SetScheduleSlotCore(DayOfWeek day, int hour, bool active)
    {
        int slot = CloudSyncSchedule.ToSlot(day, hour);
        if (active)
            EditScheduleSlots.Add(slot);
        else
            EditScheduleSlots.Remove(slot);
    }

    /// <summary>
    /// Validates permission, provider, folder existence, and path conflicts,
    /// then issues a context-bound ticket for the provider authorization endpoint.
    /// </summary>
    public async Task<string?> BuildAuthorizationUriAsync()
    {
        ErrorMessage = null;
        var share = Shares.FirstOrDefault(candidate => candidate.Id == NewShareId);
        var provider = Providers.FirstOrDefault(candidate =>
            string.Equals(candidate.Id, NewProviderId, StringComparison.OrdinalIgnoreCase));
        var localPath = CloudSyncPaths.Normalize(NewLocalPath);

        if (share is null || !HasPermission(share.Id, ManagementPermission.CreateSyncs))
        {
            ErrorMessage = Text("Web_CloudSync_Error_ShareRequired", "Select a share you may manage.");
            return null;
        }

        if (provider is null)
        {
            ErrorMessage = Text("Web_CloudSync_Error_ProviderRequired", "Select an available cloud provider.");
            return null;
        }

        var conflict = CloudSyncPaths.FindConflict(share.CloudSettings, localPath);
        if (conflict is not null)
        {
            ErrorMessage = Text("Web_CloudSync_Error_Conflict", "This folder overlaps an existing cloud sync.");
            return null;
        }

        try
        {
            await ValidateLocalDirectoryAsync(share, localPath);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Invalid local cloud-sync directory {Path} on share {ShareId}",
                localPath, share.Id);
            ErrorMessage = Text("Web_CloudSync_Error_LocalFolder", "Select an existing local folder.");
            return null;
        }

        var ticket = await _authorizationTickets.IssueAsync(
            share.Id, localPath, provider.Id, _actor!.User.Id, share.DepartmentId);
        return $"{provider.AuthorizationEndpoint}?shareId={Uri.EscapeDataString(share.Id.ToString())}" +
               $"&path={Uri.EscapeDataString(localPath)}" +
               $"&ticket={Uri.EscapeDataString(ticket)}";
    }

    /// <summary>
    /// Revalidates configure permission and atomically replaces the selected
    /// local-path key while preserving its provider credentials.
    /// </summary>
    public async Task<bool> SaveSelectedAsync()
    {
        if (SelectedSync is null || _actor is null)
            return false;

        ErrorMessage = null;
        if (!await _managementAuth.CanManageShareAsync(
                _actor, SelectedSync.ShareId, ManagementPermission.ConfigureSyncs))
        {
            ErrorMessage = Text("Web_CloudSync_Error_NotAuthorized", "You are not authorized for this action.");
            return false;
        }

        if (EditScheduleEnabled && EditScheduleSlots.Count == 0)
        {
            ErrorMessage = Text(
                "Web_CloudSync_Schedule_Error_Empty",
                "Select at least one hour before enabling the timer.");
            return false;
        }
        if (EditScheduleIntervalSeconds is < CloudSyncSchedule.MinIntervalSeconds
            or > CloudSyncSchedule.MaxIntervalSeconds)
        {
            ErrorMessage = Text(
                "Web_CloudSync_Schedule_Error_Interval",
                "Enter an interval between 1 and 86400 seconds.");
            return false;
        }
        if (EditMaxFileSizeMb is < 0 || EditMaxUploadRateKbps is < 0 || EditMaxDownloadRateKbps is < 0)
        {
            ErrorMessage = Text("Web_CloudSync_Advanced_Error_Negative", "Advanced limits cannot be negative.");
            return false;
        }
        if (RequiresRemoteFolderSelection && !_remoteFolderSelectedExplicitly)
        {
            ErrorMessage = Text(
                "Web_CloudSync_RemoteFolder_Required",
                "Select a remote folder before saving this cloud sync. The root folder is allowed.");
            return false;
        }

        var selected = SelectedSync;
        var newLocalPath = CloudSyncPaths.Normalize(EditLocalPath);
        var operationLease = await _syncOperations.TryBeginPathMutationAsync(
            selected.ShareId, selected.LocalPath, newLocalPath);
        if (operationLease is null)
        {
            ErrorMessage = Text(
                "Web_CloudSync_Error_Busy",
                "This folder is already being synchronized or modified.");
            return false;
        }
        await using var operation = operationLease;

        // Load only after the lease was acquired. Otherwise a sync that finishes
        // between loading and saving could have its LastSync/token update reverted.
        var share = await _shareRepository.GetByIdAsync(selected.ShareId);
        if (share is null || !share.CloudSettings.Folders.TryGetValue(
                selected.LocalPath, out var folder))
        {
            ErrorMessage = Text("Web_CloudSync_Error_Missing", "This cloud sync no longer exists.");
            return false;
        }

        var conflict = share.CloudSettings.Folders.Keys.FirstOrDefault(existing =>
            !string.Equals(existing, selected.LocalPath, StringComparison.OrdinalIgnoreCase)
            && (CloudSyncPaths.IsSameOrAncestor(existing, newLocalPath)
                || CloudSyncPaths.IsSameOrAncestor(newLocalPath, existing)));
        if (conflict is not null)
        {
            ErrorMessage = Text("Web_CloudSync_Error_Conflict", "This folder overlaps an existing cloud sync.");
            return false;
        }

        await ValidateLocalDirectoryAsync(share, newLocalPath);

        share.CloudSettings.Folders.Remove(selected.LocalPath);
        folder.RemotePath = NormalizeRemotePath(EditRemotePath);
        folder.DisplayName = EditDisplayName.Trim();
        folder.Description = EditDescription.Trim();
        folder.RequiresRemoteFolderSelection = false;
        folder.Mode = EditMode;
        folder.AdvancedSettings = new CloudSyncAdvancedSettings
        {
            MaxFileSizeBytes = ToBytes(EditMaxFileSizeMb, 1024L * 1024),
            ExcludedExtensions = ParseExtensions(EditExcludedExtensions),
            MaxUploadBytesPerSecond = ToBytes(EditMaxUploadRateKbps, 1024L),
            MaxDownloadBytesPerSecond = ToBytes(EditMaxDownloadRateKbps, 1024L),
            SyncDeletions = EditSyncDeletions
        };
        var previousSchedule = folder.Schedule ?? new CloudSyncSchedule();
        folder.Schedule = new CloudSyncSchedule
        {
            IsEnabled = EditScheduleEnabled,
            IntervalSeconds = EditScheduleIntervalSeconds,
            ActiveSlots = new HashSet<int>(EditScheduleSlots.Where(CloudSyncSchedule.IsValidSlot)),
            RunAsUsername = EditScheduleEnabled
                ? _actor.User.Username
                : previousSchedule.RunAsUsername
        };
        share.CloudSettings.Folders[newLocalPath] = folder;
        await _shareRepository.UpdateAsync(share);
        _schedulerSignal.Wake();

        await LoadAsync(share.Id, newLocalPath);
        return true;
    }

    /// <summary>
    /// Removes the selected mapping after permission validation. Provider cleanup
    /// is best-effort so an unavailable cloud service cannot trap local settings.
    /// </summary>
    public async Task<bool> DeleteSelectedAsync()
    {
        if (SelectedSync is null || _actor is null)
            return false;

        var selected = SelectedSync;
        if (!await _managementAuth.CanManageShareAsync(
                _actor, selected.ShareId, ManagementPermission.DeleteSyncs))
        {
            ErrorMessage = Text("Web_CloudSync_Error_NotAuthorized", "You are not authorized for this action.");
            return false;
        }

        var operationLease = await _syncOperations.TryBeginPathMutationAsync(
            selected.ShareId, selected.LocalPath, selected.LocalPath);
        if (operationLease is null)
        {
            ErrorMessage = Text(
                "Web_CloudSync_Error_Busy",
                "This folder is already being synchronized or modified.");
            return false;
        }
        await using var operation = operationLease;

        var share = await _shareRepository.GetByIdAsync(selected.ShareId);
        if (share is null || !share.CloudSettings.Folders.Remove(selected.LocalPath, out var folder))
            return false;

        try
        {
            await _providerFactory.DisposeConnectionAsync(share.Id, folder);
        }
        catch (Exception ex)
        {
            // Removing local configuration must remain possible when a provider's
            // token-revocation endpoint is temporarily unavailable.
            _logger.LogWarning(ex, "Cloud connection revocation failed for share {ShareId}", share.Id);
        }
        await _shareRepository.UpdateAsync(share);
        _schedulerSignal.Wake();
        SelectedSync = null;
        SelectedAccount = null;
        await LoadAsync();
        return true;
    }

    /// <summary>
    /// Revalidates manual-sync permission, loads the latest persisted mapping,
    /// and executes the provider-neutral sync with cooperative cancellation.
    /// LastSync is written only after the operation completes successfully.
    /// </summary>
    public async Task<bool> SyncNowAsync(
        Action<string?, int> reportProgress,
        CancellationToken cancellationToken = default)
    {
        if (SelectedSync is null || _actor is null)
            return false;

        var selected = SelectedSync;
        if (!await _managementAuth.CanManageShareAsync(
                _actor, selected.ShareId, ManagementPermission.SyncManually))
        {
            ErrorMessage = Text("Web_CloudSync_Error_NotAuthorized", "You are not authorized for this action.");
            return false;
        }

        IsBusy = true;
        LastSyncWasCancelled = false;
        ErrorMessage = null;
        try
        {
            var result = await _syncExecution.RunAsync(
                selected.ShareId,
                selected.LocalPath,
                _actor,
                reportProgress,
                cancellationToken);
            if (result == CloudSyncExecutionResult.Busy)
            {
                ErrorMessage = Text(
                    "Web_CloudSync_Error_Busy",
                    "This folder is already being synchronized or modified.");
                return false;
            }
            if (result == CloudSyncExecutionResult.Missing)
            {
                ErrorMessage = Text(
                    "Web_CloudSync_Error_Missing",
                    "This cloud sync no longer exists.");
                return false;
            }

            await LoadAsync(selected.ShareId, selected.LocalPath);
            return true;
        }
        // Cancellation is a normal user action. Do not log it as a failure and do
        // not update LastSync, because the directory pair may be only partially processed.
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            LastSyncWasCancelled = true;
            ErrorMessage = Text("Web_CloudSync_Cancelled", "Synchronization was cancelled.");
            _logger.LogInformation(
                "Manual cloud sync was cancelled for share {ShareId} and path {Path}",
                selected.ShareId,
                selected.LocalPath);
            return false;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Manual cloud sync failed for share {ShareId} and path {Path}",
                selected.ShareId, selected.LocalPath);
            ErrorMessage = Text("Web_CloudSync_Error_Run", "The cloud sync failed. Check the server log.");
            return false;
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>
    /// Lists local child directories through the share-scoped file service so
    /// normal ACL and path-containment rules remain enforced in the picker.
    /// </summary>
    public async Task<IReadOnlyList<CloudDirectoryItem>> LoadLocalDirectoriesAsync(Guid shareId, string path)
    {
        if (_actor is null)
            return [];

        var share = Shares.FirstOrDefault(candidate => candidate.Id == shareId)
            ?? throw new InvalidOperationException("The selected share is not available.");
        var normalized = CloudSyncPaths.Normalize(path);
        var fileService = _fileServiceFactory.CreateForShare(share.Id, share.Path);
        var items = await fileService.ListAsync(normalized, _actor);

        return items
            .Where(item => item.IsDirectory)
            .OrderBy(item => item.Name, StringComparer.CurrentCultureIgnoreCase)
            .Select(item => new CloudDirectoryItem(
                item.Name,
                CombineLocalPath(normalized, item.Name)))
            .ToArray();
    }

    /// <summary>
    /// Lists remote child directories through the selected provider abstraction
    /// and persists a refresh token if the provider rotated it during the call.
    /// </summary>
    public async Task<IReadOnlyList<CloudDirectoryItem>> LoadRemoteDirectoriesAsync(string path)
    {
        if (SelectedSync is null)
            return [];

        var selected = SelectedSync;
        var normalized = NormalizeRemotePath(path);
        try
        {
            var connection = _providerFactory.CreateOrLoad(
                selected.ShareId, selected.Configuration);
            var items = await connection.ListAsync(normalized);
            await PersistPendingCredentialsAsync(
                selected.ShareId,
                selected.LocalPath,
                connection);

            return items
                .Where(item => item.IsDirectory)
                .OrderBy(item => item.Name, StringComparer.CurrentCultureIgnoreCase)
                .Select(item => new CloudDirectoryItem(item.Name, NormalizeRemotePath(item.Path)))
                .ToArray();
        }
        catch (Exception exception)
        {
            _logger.LogWarning(
                exception,
                "Unable to list remote cloud directory {RemotePath} for share {ShareId}",
                normalized,
                selected.ShareId);
            throw;
        }
    }

    /// <summary>
    /// Merges provider-rotated credentials into a freshly loaded share and only
    /// acknowledges them after repository persistence succeeds.
    /// </summary>
    private async Task PersistPendingCredentialsAsync(
        Guid shareId,
        string localPath,
        ICloudConnection connection)
    {
        if (!connection.HasPendingCredentialChanges)
            return;

        bool persisted = await _shareRepository.UpdateCloudSyncRuntimeStateAsync(
            shareId,
            localPath,
            lastSync: null,
            connection.GetPendingCredentialChanges());
        if (persisted)
            connection.AcknowledgeCredentialChanges();
    }

    /// <summary>Ensures a mapping targets an existing directory visible to the actor.</summary>
    private async Task ValidateLocalDirectoryAsync(ShareDefinition share, string localPath)
    {
        if (_actor is null)
            return;

        var metadata = await _fileServiceFactory
            .CreateForShare(share.Id, share.Path)
            .GetMetadataAsync(localPath, _actor);
        if (!metadata.IsDirectory)
            throw new InvalidOperationException("A cloud sync must target a local directory.");
    }

    /// <summary>Restores selection after a list reload using stable share/path identity.</summary>
    private async Task SelectMatchingAsync(Guid shareId, string localPath)
    {
        var match = Syncs.FirstOrDefault(item => item.ShareId == shareId
            && string.Equals(item.LocalPath, localPath, StringComparison.OrdinalIgnoreCase));
        if (match is not null)
            await SelectAsync(match);
        else
            Deselect();
    }

    /// <summary>Flattens per-share mappings into the sorted list rendered by Razor.</summary>
    private void RebuildSyncList()
    {
        Syncs = Shares
            .SelectMany(share => share.CloudSettings.Folders.Select(entry =>
                new CloudSyncListItem(
                    share.Id,
                    share.Name,
                    CloudSyncPaths.Normalize(entry.Key),
                    entry.Value,
                    Providers.FirstOrDefault(provider => string.Equals(
                        provider.Id, entry.Value.Provider, StringComparison.OrdinalIgnoreCase))?.DisplayName
                        ?? entry.Value.Provider)))
            .OrderBy(item => item.ShareName, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(item => item.LocalPath, StringComparer.CurrentCultureIgnoreCase)
            .ToArray();
    }

    private bool HasPermission(Guid shareId, ManagementPermission permission)
        => _permissions.TryGetValue(shareId, out var granted) && (granted & permission) == permission;

    private static string CombineLocalPath(string parent, string child)
        => parent.Length == 0 ? child : $"{parent}/{child}";

    /// <summary>Normalizes remote paths to exactly one leading slash and no trailing slash.</summary>
    public static string NormalizeRemotePath(string? path)
    {
        var normalized = string.IsNullOrWhiteSpace(path)
            ? ""
            : path.Replace('\\', '/').Trim('/');
        return normalized.Length == 0 ? "/" : $"/{normalized}";
    }

    private static HashSet<string> ParseExtensions(string? value)
        => (value ?? "").Split([',', ';', '\n', '\r', ' '], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(extension => extension.StartsWith('.') ? extension : $".{extension}")
            .Where(extension => extension.Length > 1)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

    private static long? ToBytes(long? value, long multiplier)
        => value is > 0 ? checked(value.Value * multiplier) : null;

    private static long? ToMegabytes(long? bytes)
        => bytes is > 0 ? (long)Math.Ceiling(bytes.Value / (1024d * 1024d)) : null;

    private static long? ToKilobytes(long? bytes)
        => bytes is > 0 ? (long)Math.Ceiling(bytes.Value / 1024d) : null;

    private static string Text(string key, string fallback)
        => Resources.ResourceManager.GetString(key) ?? fallback;
}

/// <summary>Provider-neutral row displayed in the cloud-sync inventory.</summary>
public sealed record CloudSyncListItem(
    Guid ShareId,
    string ShareName,
    string LocalPath,
    SyncedFolder Configuration,
    string ProviderDisplayName);

/// <summary>Minimal directory-picker item shared by local and remote sources.</summary>
public sealed record CloudDirectoryItem(string Name, string Path);

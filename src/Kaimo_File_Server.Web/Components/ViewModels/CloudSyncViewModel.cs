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
    private readonly IManagementAuthService _managementAuth;
    private readonly IUserContextFactory _userContextFactory;
    private readonly AuthenticationStateProvider _authenticationState;
    private readonly ICloudAuthorizationTicketStore _authorizationTickets;
    private readonly ILogger<CloudSyncViewModel> _logger;
    private readonly Dictionary<Guid, ManagementPermission> _permissions = [];
    private UserContext? _actor;

    /// <summary>Creates the provider-neutral administration model and its security dependencies.</summary>
    public CloudSyncViewModel(
        IShareRepository shareRepository,
        ICloudProviderFactory providerFactory,
        IFileServiceFactory fileServiceFactory,
        IManagementAuthService managementAuth,
        IUserContextFactory userContextFactory,
        AuthenticationStateProvider authenticationState,
        ICloudAuthorizationTicketStore authorizationTickets,
        ILogger<CloudSyncViewModel> logger)
    {
        _shareRepository = shareRepository;
        _providerFactory = providerFactory;
        _fileServiceFactory = fileServiceFactory;
        _managementAuth = managementAuth;
        _userContextFactory = userContextFactory;
        _authenticationState = authenticationState;
        _authorizationTickets = authorizationTickets;
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
    public SyncMode EditMode { get; set; } = SyncMode.TwoWay;

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
        EditMode = item.Configuration.Mode;
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
        ErrorMessage = null;
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

        var ticket = _authorizationTickets.Issue(share.Id, localPath, provider.Id);
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

        var share = await _shareRepository.GetByIdAsync(SelectedSync.ShareId);
        if (share is null || !share.CloudSettings.Folders.TryGetValue(
                SelectedSync.LocalPath, out var folder))
        {
            ErrorMessage = Text("Web_CloudSync_Error_Missing", "This cloud sync no longer exists.");
            return false;
        }

        var newLocalPath = CloudSyncPaths.Normalize(EditLocalPath);
        var conflict = share.CloudSettings.Folders.Keys.FirstOrDefault(existing =>
            !string.Equals(existing, SelectedSync.LocalPath, StringComparison.OrdinalIgnoreCase)
            && (CloudSyncPaths.IsSameOrAncestor(existing, newLocalPath)
                || CloudSyncPaths.IsSameOrAncestor(newLocalPath, existing)));
        if (conflict is not null)
        {
            ErrorMessage = Text("Web_CloudSync_Error_Conflict", "This folder overlaps an existing cloud sync.");
            return false;
        }

        await ValidateLocalDirectoryAsync(share, newLocalPath);

        share.CloudSettings.Folders.Remove(SelectedSync.LocalPath);
        folder.RemotePath = NormalizeRemotePath(EditRemotePath);
        folder.Mode = EditMode;
        share.CloudSettings.Folders[newLocalPath] = folder;
        await _shareRepository.UpdateAsync(share);

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
            var share = await _shareRepository.GetByIdAsync(selected.ShareId);
            if (share is null || !share.CloudSettings.Folders.TryGetValue(
                    selected.LocalPath, out var folder))
                return false;

            var connection = _providerFactory.CreateOrLoad(share.Id, folder);
            var fileService = _fileServiceFactory.CreateForShare(share.Id, share.Path);
            await connection.SyncAsync(
                fileService,
                _actor,
                NormalizeRemotePath(folder.RemotePath),
                selected.LocalPath,
                folder.Mode,
                reportProgress,
                cancellationToken);

            cancellationToken.ThrowIfCancellationRequested();
            ApplyPendingCredentials(folder, connection);
            folder.LastSync = DateTime.UtcNow;
            await _shareRepository.UpdateAsync(share);
            connection.AcknowledgeCredentialChanges();
            await LoadAsync(share.Id, selected.LocalPath);
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

        var share = await _shareRepository.GetByIdAsync(shareId);
        if (share is null || !share.CloudSettings.Folders.TryGetValue(localPath, out var folder))
            return;

        ApplyPendingCredentials(folder, connection);
        await _shareRepository.UpdateAsync(share);
        connection.AcknowledgeCredentialChanges();
    }

    /// <summary>Applies only fields explicitly reported as rotated by the provider.</summary>
    private static void ApplyPendingCredentials(
        SyncedFolder folder,
        ICloudConnection connection)
    {
        foreach (var (key, value) in connection.GetPendingCredentialChanges())
            folder.Data[key] = value;
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

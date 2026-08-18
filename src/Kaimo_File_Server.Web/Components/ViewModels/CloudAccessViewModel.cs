using Kaimo_File_Server.Core.Domain;
using Kaimo_File_Server.Core.Domain.Department;
using Kaimo_File_Server.Core.Domain.Identity;
using Kaimo_File_Server.Core.Helpers;
using Kaimo_File_Server.Core.Language;
using Kaimo_File_Server.Core.Repositories;
using Kaimo_File_Server.Core.Security;
using Kaimo_File_Server.Core.Services;
using Kaimo_File_Server.Core.Services.ExternalStorage;
using Kaimo_File_Server.Infrastructure.Clouds;
using Kaimo_File_Server.Web.Services;
using Microsoft.AspNetCore.Components.Authorization;

namespace Kaimo_File_Server.Web.Components.ViewModels;

public sealed class CloudAccessViewModel
{
    private readonly ICloudAccessRepository _repository;
    private readonly IStorageConnectionRepository _connections;
    private readonly ICloudAuthorizationTicketStore _tickets;
    private readonly IManagementAuthService _managementAuth;
    private readonly IUserContextFactory _userContextFactory;
    private readonly AuthenticationStateProvider _authenticationState;
    private readonly IDepartmentRepository _departments;
    private readonly IUserRepository _users;
    private readonly IGroupRepository _groups;
    private readonly IShareRepository _localShares;
    private readonly OneDriveStorageConnectionFactory _oneDriveConnections;
    private readonly ILogger<CloudAccessViewModel> _logger;
    private readonly IStorageConnectionProviderCatalog? _providerCatalog;
    private UserContext? _actor;

    public CloudAccessViewModel(
        ICloudAccessRepository repository,
        IStorageConnectionRepository connections,
        ICloudAuthorizationTicketStore tickets,
        IManagementAuthService managementAuth,
        IUserContextFactory userContextFactory,
        AuthenticationStateProvider authenticationState,
        IDepartmentRepository departments,
        IUserRepository users,
        IGroupRepository groups,
        IShareRepository localShares,
        OneDriveStorageConnectionFactory oneDriveConnections,
        ILogger<CloudAccessViewModel> logger,
        IStorageConnectionProviderCatalog? providerCatalog = null)
    {
        _repository = repository;
        _connections = connections;
        _tickets = tickets;
        _managementAuth = managementAuth;
        _userContextFactory = userContextFactory;
        _authenticationState = authenticationState;
        _departments = departments;
        _users = users;
        _groups = groups;
        _localShares = localShares;
        _oneDriveConnections = oneDriveConnections;
        _logger = logger;
        _providerCatalog = providerCatalog;
    }

    public List<StorageConnection> Connections { get; private set; } = [];
    public IReadOnlyDictionary<Guid, StorageConnectionUsage> ConnectionUsage { get; private set; }
        = new Dictionary<Guid, StorageConnectionUsage>();
    public List<CloudAccessShare> Shares { get; private set; } = [];
    public List<Department> ManageableDepartments { get; private set; } = [];
    public List<Identity> AvailablePrincipals { get; private set; } = [];
    public bool IsLoading { get; private set; }
    public bool CanManage { get; private set; }
    public bool CanManageConnections { get; private set; }
    public bool CanManageVirtualShares { get; private set; }
    public bool CanUseConnections { get; private set; }
    public string? ErrorMessage { get; private set; }

    public async Task LoadAsync()
    {
        IsLoading = true;
        ErrorMessage = null;
        try
        {
            _actor = await GetActorAsync();
            if (_actor is null) return;
            var connectionScope = await _managementAuth.GetAuthorizedDepartmentIdsAsync(
                _actor, ManagementPermission.ManageConnections);
            var usageScope = await _managementAuth.GetAuthorizedDepartmentIdsAsync(
                _actor, ManagementPermission.UseConnections);
            var shareScope = await _managementAuth.GetAuthorizedDepartmentIdsAsync(
                _actor, ManagementPermission.ManageCloudAccess);
            CanManageConnections = HasDepartments(connectionScope);
            CanUseConnections = HasDepartments(usageScope);
            CanManageVirtualShares = HasDepartments(shareScope);
            CanManage = CanManageConnections || CanManageVirtualShares || CanUseConnections;
            var allDepartments = await _departments.GetAllAsync();
            var connectionManagementIds = GetDepartmentIds(connectionScope, allDepartments);
            var connectionUsageIds = GetDepartmentIds(usageScope, allDepartments);
            var shareIds = GetDepartmentIds(shareScope, allDepartments);
            ManageableDepartments = allDepartments.Where(x => connectionManagementIds.Contains(x.Id))
                .OrderBy(x => x.Name).ToList();
            var visibleConnectionIds = connectionManagementIds.Concat(connectionUsageIds).ToHashSet();
            Connections = (await _connections.GetAllAsync())
                .Where(x => visibleConnectionIds.Contains(x.DepartmentId)).ToList();
            ConnectionUsage = (await Task.WhenAll(Connections.Select(async connection =>
                    new KeyValuePair<Guid, StorageConnectionUsage>(
                        connection.Id,
                        await _connections.GetUsageAsync(connection.Id)))))
                .ToDictionary(item => item.Key, item => item.Value);
            Shares = (await _repository.GetSharesAsync())
                .Where(x => shareIds.Contains(x.DepartmentId)).ToList();
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "Unable to load Cloud Access administration");
            ErrorMessage = R("Web_CloudAccess_LoadFailed");
        }
        finally { IsLoading = false; }
    }

    public async Task<string> CreateOneDriveConnectionAsync(string name, Guid departmentId)
    {
        await EnsureCanManageConnectionDepartmentAsync(departmentId);
        if (string.IsNullOrWhiteSpace(name) || name.Trim().Length > 200)
            throw new ArgumentException(R("Web_CloudAccess_InvalidConnectionName"));
        var connection = new StorageConnection
        {
            DepartmentId = departmentId,
            CreatedByUserId = _actor!.User.Id,
            ProviderProfileId = WellKnownProviderProfiles.MicrosoftPublicClient,
            ProviderId = "onedrive",
            Name = name.Trim(),
            AuthorizationMode = StorageAuthorizationMode.DeviceCode,
            State = StorageConnectionState.PendingAuthorization
        };
        await _connections.SaveAsync(connection);
        var ticket = await _tickets.IssueAsync(
            connection.Id, string.Empty, "onedrive-access", _actor.User.Id, connection.DepartmentId);
        return $"/api/cloud-access/onedrive/connect?connectionId={connection.Id}&ticket={Uri.EscapeDataString(ticket)}";
    }

    /// <summary>
    /// Creates an operator-configured protocol connection without accepting
    /// inline credentials. The provider validates mount attestations or secret
    /// references before the connection can enter the ready state.
    /// </summary>
    public async Task CreateConfiguredConnectionAsync(
        string providerId,
        string name,
        Guid departmentId,
        string settingsJson)
    {
        await EnsureCanManageConnectionDepartmentAsync(departmentId);
        if (_providerCatalog is null)
            throw new NotSupportedException(R("Web_ExternalStorage_ProviderUnsupported"));
        var provider = _providerCatalog.GetRequired(providerId);
        var authorizationMode = provider.AuthorizationModes.SingleOrDefault(mode =>
            mode is StorageAuthorizationMode.HostMount or StorageAuthorizationMode.SshKey);
        if (authorizationMode is not (StorageAuthorizationMode.HostMount or StorageAuthorizationMode.SshKey))
            throw new NotSupportedException(R("Web_ExternalStorage_ProviderUnsupported"));
        string normalizedName = name.Trim();
        if (string.IsNullOrWhiteSpace(normalizedName) || normalizedName.Length > 200)
            throw new ArgumentException(R("Web_CloudAccess_InvalidConnectionName"));
        if (string.IsNullOrWhiteSpace(settingsJson) || settingsJson.Length > 64 * 1024)
            throw new ArgumentException(R("Web_ExternalStorage_InvalidSettings"));

        var connection = new StorageConnection
        {
            DepartmentId = departmentId,
            CreatedByUserId = _actor!.User.Id,
            ProviderId = provider.Id,
            Name = normalizedName,
            AuthorizationMode = authorizationMode,
            SettingsJson = settingsJson,
            State = StorageConnectionState.PendingConfiguration
        };
        var health = await provider.TestAsync(connection);
        connection.State = MapUnhealthyState(connection, health);
        connection.LastVerifiedAtUtc = health.CheckedAtUtc;
        connection.LastErrorCode = health.IsHealthy ? null : health.Code;
        await _connections.SaveAsync(connection);
        await LoadAsync();
    }

    public async Task<string> AuthorizeConnectionAsync(Guid connectionId)
    {
        var connection = await GetManagedConnectionAsync(connectionId);
        var provider = _providerCatalog?.GetRequired(connection.ProviderId);
        if (provider is not null
            && (!provider.Capabilities.HasFlag(StorageProviderCapabilities.DelegatedAuthorization)
                || connection.AuthorizationMode != StorageAuthorizationMode.DeviceCode))
            throw new NotSupportedException(R("Web_ExternalStorage_AuthorizeUnsupported"));
        var ticket = await _tickets.IssueAsync(
            connection.Id, string.Empty, "onedrive-access", _actor!.User.Id, connection.DepartmentId);
        return $"/api/cloud-access/onedrive/connect?connectionId={connection.Id}&ticket={Uri.EscapeDataString(ticket)}";
    }

    /// <summary>Updates the display name of a managed provider connection.</summary>
    public async Task UpdateConnectionAsync(Guid connectionId, string name)
    {
        var connection = await GetManagedConnectionAsync(connectionId);
        var normalizedName = name.Trim();
        if (string.IsNullOrWhiteSpace(normalizedName) || normalizedName.Length > 200)
            throw new ArgumentException(R("Web_CloudAccess_InvalidConnectionName"));

        connection.Name = normalizedName;
        await _connections.SaveAsync(connection);
        await LoadAsync();
    }

    /// <summary>Disables or re-enables a connection without removing its consumers.</summary>
    public async Task SetConnectionEnabledAsync(Guid connectionId, bool enabled)
    {
        var connection = await GetManagedConnectionAsync(connectionId);
        connection.State = enabled
            ? string.IsNullOrWhiteSpace(connection.EncryptedCredentialPayload)
                ? StorageConnectionState.PendingAuthorization
                : StorageConnectionState.Ready
            : StorageConnectionState.Disabled;
        connection.LastErrorCode = null;
        await _connections.SaveAsync(connection);
        await LoadAsync();
    }

    /// <summary>
    /// Verifies a connection through its provider-neutral health contract.
    /// Provider response details remain behind the sanitized provider boundary.
    /// </summary>
    public async Task TestConnectionAsync(Guid connectionId)
    {
        var connection = await GetManagedConnectionAsync(connectionId);
        if (_providerCatalog is null)
        {
            if (!string.Equals(connection.ProviderId, "onedrive", StringComparison.OrdinalIgnoreCase))
                throw new NotSupportedException(R("Web_ExternalStorage_TestUnsupported"));
            await ListOneDriveFoldersAsync(connectionId, "/");
            connection = await GetManagedConnectionAsync(connectionId);
            connection.State = StorageConnectionState.Ready;
            connection.LastVerifiedAtUtc = DateTime.UtcNow;
            connection.LastErrorCode = null;
        }
        else
        {
            var result = await _providerCatalog.GetRequired(connection.ProviderId).TestAsync(connection);
            connection.State = MapUnhealthyState(connection, result);
            connection.LastVerifiedAtUtc = result.CheckedAtUtc;
            connection.LastErrorCode = result.IsHealthy ? null : result.Code;
        }
        await _connections.SaveAsync(connection);
        await LoadAsync();
    }

    public async Task<List<CloudDirectoryItem>> ListOneDriveFoldersAsync(Guid connectionId, string path)
    {
        var connectionRecord = await GetManagedConnectionAsync(connectionId);
        if (connectionRecord.State != StorageConnectionState.Ready
            || string.IsNullOrWhiteSpace(connectionRecord.EncryptedCredentialPayload))
            throw new InvalidOperationException(R("Web_CloudAccess_ConnectionNotReady"));
        if (!ShareRelativePath.TryNormalizeStrict(path.Trim('/'), out var normalized))
            throw new UnauthorizedAccessException(R("Web_CloudAccess_InvalidRemotePath"));
        await using var connection = _oneDriveConnections.Create(connectionRecord);
        var items = await connection.ListDetailedAsync(normalized);
        return items.Where(x => x.IsDirectory)
            .Select(x => new CloudDirectoryItem(x.Name, "/" + x.Path.Trim('/')))
            .OrderBy(x => x.Name).ToList();
    }

    public async Task LoadPrincipalsAsync(Guid departmentId)
    {
        await EnsureCanManageDepartmentAsync(departmentId);
        var departmentIds = (await _departments.GetDescendantIdsAsync(departmentId)).Append(departmentId).ToHashSet();
        var users = new Dictionary<Guid, User>();
        var groups = new Dictionary<Guid, Group>();
        foreach (var id in departmentIds)
        {
            foreach (var user in await _departments.GetUsersAsync(id)) users[user.Id] = user;
            foreach (var group in await _departments.GetGroupsAsync(id)) groups[group.Id] = group;
        }
        AvailablePrincipals = users.Values.Cast<Identity>().Concat(groups.Values)
            .OrderBy(x => x is Group ? 1 : 0).ThenBy(x => x.Name).ToList();
    }

    public async Task CreateShareAsync(
        Guid connectionId,
        string name,
        string remoteRootPath,
        bool isReadOnly,
        IEnumerable<Guid> principalIds)
    {
        var connectionRecord = await GetUsableConnectionAsync(connectionId);
        if (_providerCatalog is not null
            && !_providerCatalog.GetRequired(connectionRecord.ProviderId).Capabilities
                .HasFlag(StorageProviderCapabilities.StableItemIds))
            throw new NotSupportedException(R("Web_ExternalStorage_VirtualShareUnsupported"));
        if (connectionRecord.State != StorageConnectionState.Ready
            || string.IsNullOrWhiteSpace(connectionRecord.EncryptedCredentialPayload))
            throw new InvalidOperationException(R("Web_CloudAccess_ConnectionNotReady"));
        var normalizedName = name.Trim();
        if (string.IsNullOrWhiteSpace(normalizedName)
            || normalizedName.Length > 200
            || !SambaName.IsValidShareName(normalizedName))
            throw new ArgumentException(R("Web_CloudAccess_InvalidShareName"));
        if (!await IsShareNameAvailableAsync(normalizedName))
            throw new InvalidOperationException(R("Web_CloudAccess_ShareNameExists"));
        if (!ShareRelativePath.TryNormalizeStrict(remoteRootPath.Trim('/'), out var root))
            throw new UnauthorizedAccessException(R("Web_CloudAccess_InvalidRemotePath"));

        await using var connection = _oneDriveConnections.Create(connectionRecord);
        var folder = await connection.ResolveFolderAsync(root);

        var share = new CloudAccessShare
        {
            ConnectionId = connectionId,
            DepartmentId = connectionRecord.DepartmentId,
            Name = normalizedName,
            RemoteRootPath = folder.Path,
            RemoteRootItemId = folder.ProviderId,
            IsReadOnly = isReadOnly,
            IsEnabled = true
        };
        await _repository.UpsertShareAsync(share);
        var allowed = AvailablePrincipals.Select(x => x.Id).ToHashSet();
        var grants = principalIds.Distinct().Where(allowed.Contains)
            .Select(id => new CloudAccessGrant
            {
                ShareId = share.Id,
                PrincipalId = id,
                GrantedByUserId = _actor!.User.Id
            });
        await _repository.ReplaceGrantsAsync(share.Id, grants);
        await LoadAsync();
    }

    /// <summary>
    /// Updates a virtual share while preserving its identity and existing grants.
    /// The selected remote folder is resolved again to keep the persisted provider item ID current.
    /// </summary>
    public async Task UpdateShareAsync(
        Guid shareId,
        string name,
        string remoteRootPath,
        bool isReadOnly)
    {
        var share = await _repository.GetShareAsync(shareId)
                    ?? throw new InvalidOperationException(R("Web_CloudAccess_ShareMissing"));
        await EnsureCanManageDepartmentAsync(share.DepartmentId);

        var normalizedName = name.Trim();
        if (string.IsNullOrWhiteSpace(normalizedName)
            || normalizedName.Length > 200
            || !SambaName.IsValidShareName(normalizedName))
            throw new ArgumentException(R("Web_CloudAccess_InvalidShareName"));
        if (!await IsShareNameAvailableAsync(normalizedName, share.Id))
            throw new InvalidOperationException(R("Web_CloudAccess_ShareNameExists"));
        if (!ShareRelativePath.TryNormalizeStrict(remoteRootPath.Trim('/'), out var root))
            throw new UnauthorizedAccessException(R("Web_CloudAccess_InvalidRemotePath"));

        var connectionRecord = await GetUsableConnectionAsync(share.ConnectionId);
        if (connectionRecord.State != StorageConnectionState.Ready
            || string.IsNullOrWhiteSpace(connectionRecord.EncryptedCredentialPayload))
            throw new InvalidOperationException(R("Web_CloudAccess_ConnectionNotReady"));

        await using var connection = _oneDriveConnections.Create(connectionRecord);
        var folder = await connection.ResolveFolderAsync(root);

        share.Name = normalizedName;
        share.RemoteRootPath = folder.Path;
        share.RemoteRootItemId = folder.ProviderId;
        share.IsReadOnly = isReadOnly;
        await _repository.UpsertShareAsync(share);
        await LoadAsync();
    }

    public async Task DeleteShareAsync(Guid shareId)
    {
        var share = await _repository.GetShareAsync(shareId)
                    ?? throw new InvalidOperationException(R("Web_CloudAccess_ShareMissing"));
        await EnsureCanManageDepartmentAsync(share.DepartmentId);
        await _repository.DeleteShareAsync(shareId);
        await LoadAsync();
    }

    public async Task<HashSet<Guid>> LoadShareGrantsAsync(Guid shareId)
    {
        var share = await _repository.GetShareAsync(shareId)
                    ?? throw new InvalidOperationException(R("Web_CloudAccess_ShareMissing"));
        await EnsureCanManageDepartmentAsync(share.DepartmentId);
        await LoadPrincipalsAsync(share.DepartmentId);
        var allowed = AvailablePrincipals.Select(x => x.Id).ToHashSet();
        var current = await _repository.GetPrincipalIdsAsync(shareId);
        current.IntersectWith(allowed);
        return current;
    }

    public async Task UpdateShareGrantsAsync(Guid shareId, IEnumerable<Guid> principalIds)
    {
        var share = await _repository.GetShareAsync(shareId)
                    ?? throw new InvalidOperationException(R("Web_CloudAccess_ShareMissing"));
        await EnsureCanManageDepartmentAsync(share.DepartmentId);
        await LoadPrincipalsAsync(share.DepartmentId);
        var allowed = AvailablePrincipals.Select(x => x.Id).ToHashSet();
        var grants = principalIds.Distinct().Where(allowed.Contains).Select(id => new CloudAccessGrant
        {
            ShareId = shareId,
            PrincipalId = id,
            GrantedByUserId = _actor!.User.Id
        });
        await _repository.ReplaceGrantsAsync(shareId, grants);
    }

    public async Task DeleteConnectionAsync(Guid connectionId)
    {
        var connection = await GetManagedConnectionAsync(connectionId);
        var result = await _connections.DeleteAsync(connection.Id);
        if (result == StorageConnectionDeleteResult.InUse)
            throw new InvalidOperationException(R("Web_StorageConnection_DeleteInUse"));
        await LoadAsync();
    }

    private async Task<StorageConnection> GetManagedConnectionAsync(Guid connectionId)
    {
        var connection = await _connections.GetAsync(connectionId)
                         ?? throw new InvalidOperationException(R("Web_CloudAccess_ConnectionMissing"));
        await EnsureCanManageConnectionDepartmentAsync(connection.DepartmentId);
        return connection;
    }

    private async Task<StorageConnection> GetUsableConnectionAsync(Guid connectionId)
    {
        var connection = await _connections.GetAsync(connectionId)
                         ?? throw new InvalidOperationException(R("Web_CloudAccess_ConnectionMissing"));
        await EnsureCanManageDepartmentAsync(connection.DepartmentId);
        _actor ??= await GetActorAsync();
        if (_actor is null || !await _managementAuth.CanManageDepartmentAsync(
                _actor, connection.DepartmentId, ManagementPermission.UseConnections))
            throw new UnauthorizedAccessException(R("Web_StorageConnection_UseDenied"));
        return connection;
    }

    private async Task EnsureCanManageConnectionDepartmentAsync(Guid departmentId)
    {
        _actor ??= await GetActorAsync();
        if (_actor is null || !await _managementAuth.CanManageDepartmentAsync(
                _actor, departmentId, ManagementPermission.ManageConnections))
            throw new UnauthorizedAccessException(R("Web_StorageConnection_ManageDenied"));
    }

    private async Task EnsureCanManageDepartmentAsync(Guid departmentId)
    {
        _actor ??= await GetActorAsync();
        if (_actor is null || !await _managementAuth.CanManageDepartmentAsync(
                _actor, departmentId, ManagementPermission.ManageCloudAccess))
            throw new UnauthorizedAccessException(R("Web_CloudAccess_DepartmentDenied"));
    }

    private async Task<bool> IsShareNameAvailableAsync(string name, Guid? excludedShareId = null)
    {
        var comparer = StringComparer.OrdinalIgnoreCase;
        if ((await _repository.GetSharesAsync()).Any(x => x.Id != excludedShareId && comparer.Equals(x.Name, name)))
            return false;

        return !(await _localShares.GetAllAsync()).Any(x => comparer.Equals(x.Name, name));
    }

    private async Task<UserContext?> GetActorAsync()
    {
        var state = await _authenticationState.GetAuthenticationStateAsync();
        var username = state.User.Identity?.Name;
        return string.IsNullOrWhiteSpace(username) ? null : await _userContextFactory.CreateByUsernameAsync(username);
    }

    private static bool HasDepartments(AuthorizedScopeResult scope)
        => scope.IsUnrestricted || scope.ScopeIds.Count > 0;

    private static StorageConnectionState MapUnhealthyState(
        StorageConnection connection,
        StorageConnectionHealthResult health)
    {
        if (health.IsHealthy)
            return StorageConnectionState.Ready;
        if (connection.AuthorizationMode is StorageAuthorizationMode.HostMount or StorageAuthorizationMode.SshKey)
            return StorageConnectionState.PendingConfiguration;
        return health.State == StorageConnectionHealthState.IdentityMismatch
            ? StorageConnectionState.NeedsReauthorization
            : StorageConnectionState.Degraded;
    }

    private static HashSet<Guid> GetDepartmentIds(
        AuthorizedScopeResult scope,
        IReadOnlyCollection<Department> allDepartments)
        => scope.IsUnrestricted
            ? allDepartments.Select(department => department.Id).ToHashSet()
            : scope.ScopeIds.ToHashSet();

    private static string R(string key) => Resources.ResourceManager.GetString(key) ?? key;
}

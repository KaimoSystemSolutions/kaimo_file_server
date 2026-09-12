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
    private readonly ICredentialVault? _credentialVault;
    private readonly IStorageDirectoryTargetResolver? _directoryTargets;
    private readonly CloudAccessAuthorizationService? _cloudAuthorization;
    private UserContext? _actor;
    private AuthorizedScopeResult? _shareScope;
    private Dictionary<Guid, StorageConnectionState> _connectionStates = [];

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
        IStorageConnectionProviderCatalog? providerCatalog = null,
        ICredentialVault? credentialVault = null,
        IStorageDirectoryTargetResolver? directoryTargets = null,
        CloudAccessAuthorizationService? cloudAuthorization = null)
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
        _cloudAuthorization = cloudAuthorization;
        _providerCatalog = providerCatalog;
        _credentialVault = credentialVault;
        _directoryTargets = directoryTargets;
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

    /// <summary>
    /// True if the actor may manage the given share (edit/delete/grants). A share the
    /// actor only has access to via a grant returns false, so the UI can show it in the
    /// list without exposing its management details.
    /// </summary>
    public bool CanManageShare(CloudAccessShare share)
        => _shareScope is not null
           && (_shareScope.IsUnrestricted || _shareScope.ScopeIds.Contains(share.DepartmentId));

    /// <summary>
    /// A virtual share is usable when it is enabled and its backing connection is ready.
    /// Resolves against the full connection-state map, so it is correct even for shares
    /// the actor only has access to (whose connection is not in <see cref="Connections"/>).
    /// </summary>
    public bool IsShareUsable(CloudAccessShare share)
        => share.IsEnabled
           && _connectionStates.TryGetValue(share.ConnectionId, out var state)
           && state == StorageConnectionState.Ready;

    public async Task LoadAsync()
    {
        IsLoading = true;
        ErrorMessage = null;
        try
        {
            _actor = await GetActorAsync();
            if (_actor is null) return;
            // Connections are global: no department anchor, so management and use are
            // global-scope permissions. Only virtual shares carry a department.
            CanManageConnections = await _managementAuth.HasGlobalPermissionAsync(
                _actor, ManagementPermission.ManageConnections);
            CanUseConnections = await _managementAuth.HasGlobalPermissionAsync(
                _actor, ManagementPermission.UseConnections);
            var shareScope = await _managementAuth.GetAuthorizedDepartmentIdsAsync(
                _actor, ManagementPermission.ManageCloudAccess);
            _shareScope = shareScope;
            CanManageVirtualShares = HasDepartments(shareScope);
            CanManage = CanManageConnections || CanManageVirtualShares || CanUseConnections;
            var allDepartments = await _departments.GetAllAsync();
            var shareIds = GetDepartmentIds(shareScope, allDepartments);
            // Departments a virtual share may be mapped to = where the actor can manage
            // cloud access (Global first, as the default mapping).
            ManageableDepartments = allDepartments.Where(x => shareIds.Contains(x.Id))
                .OrderByGlobalFirst().ToList();
            var allConnections = await _connections.GetAllAsync();
            // Readiness of every connection, keyed by id, so the share list can show a
            // correct status even for access-only shares whose backing connection the
            // actor cannot manage/use (and which is therefore not in Connections).
            _connectionStates = allConnections.ToDictionary(x => x.Id, x => x.State);
            Connections = CanManageConnections || CanUseConnections
                ? allConnections
                : [];
            ConnectionUsage = (await Task.WhenAll(Connections.Select(async connection =>
                    new KeyValuePair<Guid, StorageConnectionUsage>(
                        connection.Id,
                        await _connections.GetUsageAsync(connection.Id)))))
                .ToDictionary(item => item.Key, item => item.Value);
            // Managed shares (any state, incl. disabled) so managers keep full visibility,
            // unioned with shares the actor merely has access to via a grant. This lets a
            // non-manager still see the virtual shares they are allowed to open, while the
            // management controls stay gated per-share by CanManageShare.
            var managedShares = (await _repository.GetSharesAsync())
                .Where(x => shareIds.Contains(x.DepartmentId));
            var accessibleShares = _cloudAuthorization is null
                ? []
                : await _cloudAuthorization.GetVisibleSharesAsync(_actor);
            Shares = managedShares.UnionBy(accessibleShares, share => share.Id).ToList();
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "Unable to load Cloud Access administration");
            ErrorMessage = R("Web_CloudAccess_LoadFailed");
        }
        finally { IsLoading = false; }
    }

    public async Task<string> CreateOneDriveConnectionAsync(string name)
    {
        await EnsureCanManageConnectionsAsync();
        if (string.IsNullOrWhiteSpace(name) || name.Trim().Length > 200)
            throw new ArgumentException(R("Web_CloudAccess_InvalidConnectionName"));
        var connection = new StorageConnection
        {
            CreatedByUserId = _actor!.User.Id,
            ProviderProfileId = WellKnownProviderProfiles.MicrosoftPublicClient,
            ProviderId = "onedrive",
            Name = name.Trim(),
            AuthorizationMode = StorageAuthorizationMode.DeviceCode,
            State = StorageConnectionState.PendingAuthorization
        };
        await _connections.SaveAsync(connection);
        var ticket = await _tickets.IssueAsync(
            connection.Id, string.Empty, "onedrive-access", _actor.User.Id);
        return $"/api/cloud-access/onedrive/connect?connectionId={connection.Id}&ticket={Uri.EscapeDataString(ticket)}";
    }

    /// <summary>
    /// Creates a pending Dropbox connection and returns the authorization page URL.
    /// Dropbox uses PKCE with only a public application key; no secret is stored.
    /// </summary>
    public async Task<string> CreateDropboxConnectionAsync(string name)
    {
        await EnsureCanManageConnectionsAsync();
        if (string.IsNullOrWhiteSpace(name) || name.Trim().Length > 200)
            throw new ArgumentException(R("Web_CloudAccess_InvalidConnectionName"));
        var connection = new StorageConnection
        {
            CreatedByUserId = _actor!.User.Id,
            ProviderId = "dropbox",
            Name = name.Trim(),
            AuthorizationMode = StorageAuthorizationMode.DelegatedAuthorizationCode,
            State = StorageConnectionState.PendingAuthorization
        };
        await _connections.SaveAsync(connection);
        var ticket = await _tickets.IssueAsync(
            connection.Id, string.Empty, "dropbox-access", _actor.User.Id);
        return $"/api/cloud-access/dropbox/connect?connectionId={connection.Id}&ticket={Uri.EscapeDataString(ticket)}";
    }

    /// <summary>
    /// Creates a provider-configured protocol connection. Passwords are stored
    /// only in the context-bound credential vault and never in SettingsJson.
    /// </summary>
    public async Task CreateConfiguredConnectionAsync(
        string providerId,
        string name,
        string settingsJson,
        string? username = null,
        string? password = null,
        string? domain = null,
        byte[]? sshPrivateKey = null)
    {
        await EnsureCanManageConnectionsAsync();
        if (_providerCatalog is null)
            throw new NotSupportedException(R("Web_ExternalStorage_ProviderUnsupported"));
        var provider = _providerCatalog.GetRequired(providerId);
        if (provider.AuthorizationModes.Count != 1)
            throw new NotSupportedException(R("Web_ExternalStorage_ProviderUnsupported"));
        var authorizationMode = provider.AuthorizationModes.Single();
        if (authorizationMode is not (StorageAuthorizationMode.HostMount
            or StorageAuthorizationMode.SshKey
            or StorageAuthorizationMode.UsernamePassword
            or StorageAuthorizationMode.NetworkIdentity))
            throw new NotSupportedException(R("Web_ExternalStorage_ProviderUnsupported"));
        string normalizedName = name.Trim();
        if (string.IsNullOrWhiteSpace(normalizedName) || normalizedName.Length > 200)
            throw new ArgumentException(R("Web_CloudAccess_InvalidConnectionName"));
        if (string.IsNullOrWhiteSpace(settingsJson) || settingsJson.Length > 64 * 1024)
            throw new ArgumentException(R("Web_ExternalStorage_InvalidSettings"));
        bool hasUsername = !string.IsNullOrWhiteSpace(username);
        bool hasPassword = !string.IsNullOrEmpty(password);
        if (string.Equals(provider.Id, "smb", StringComparison.OrdinalIgnoreCase)
            && (!hasUsername || !hasPassword))
            throw new ArgumentException(R("Web_ExternalStorage_InvalidCredentials"));
        if (authorizationMode == StorageAuthorizationMode.SshKey
            && sshPrivateKey is { Length: > 1024 * 1024 })
            throw new ArgumentException(R("Web_ExternalStorage_InvalidPrivateKey"));

        var connection = new StorageConnection
        {
            CreatedByUserId = _actor!.User.Id,
            ProviderId = provider.Id,
            Name = normalizedName,
            AuthorizationMode = authorizationMode,
            SettingsJson = settingsJson,
            State = StorageConnectionState.PendingConfiguration
        };
        if (hasUsername || hasPassword)
        {
            if (!hasUsername || !hasPassword || _credentialVault is null)
                throw new ArgumentException(R("Web_ExternalStorage_InvalidCredentials"));
            var credentials = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["username"] = username!.Trim(),
                ["password"] = password!
            };
            if (!string.IsNullOrWhiteSpace(domain))
                credentials["domain"] = domain.Trim();
            connection.EncryptedCredentialPayload = _credentialVault.ProtectConnectionCredentials(connection, credentials);
            connection.AccountDisplayName = username.Trim();
            connection.CredentialUpdatedAtUtc = DateTime.UtcNow;
        }
        else if (sshPrivateKey is { Length: > 0 })
        {
            if (_credentialVault is null)
                throw new ArgumentException(R("Web_ExternalStorage_InvalidPrivateKey"));
            var credentials = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["privateKeyBase64"] = Convert.ToBase64String(sshPrivateKey)
            };
            connection.EncryptedCredentialPayload = _credentialVault.ProtectConnectionCredentials(
                connection, credentials);
            connection.CredentialUpdatedAtUtc = DateTime.UtcNow;
        }
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
            && !provider.Capabilities.HasFlag(StorageProviderCapabilities.DelegatedAuthorization))
            throw new NotSupportedException(R("Web_ExternalStorage_AuthorizeUnsupported"));

        // Each interactive provider owns its own authorization page and ticket
        // purpose. Device code (OneDrive) and PKCE code paste (Dropbox) differ.
        var (endpoint, purpose) = connection.AuthorizationMode switch
        {
            StorageAuthorizationMode.DeviceCode => ("onedrive", "onedrive-access"),
            StorageAuthorizationMode.DelegatedAuthorizationCode when connection.ProviderId == "dropbox"
                => ("dropbox", "dropbox-access"),
            _ => throw new NotSupportedException(R("Web_ExternalStorage_AuthorizeUnsupported"))
        };
        var ticket = await _tickets.IssueAsync(
            connection.Id, string.Empty, purpose, _actor!.User.Id);
        return $"/api/cloud-access/{endpoint}/connect?connectionId={connection.Id}&ticket={Uri.EscapeDataString(ticket)}";
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

    /// <summary>
    /// Updates a configured protocol connection in place: display name, connection
    /// settings, and — for username/password providers — the stored credentials.
    /// Leaving the password empty preserves the existing one so the operator can
    /// adjust the username, domain, or settings without re-entering it. The
    /// provider, department, and any SSH key material stay unchanged.
    /// </summary>
    public async Task UpdateConfiguredConnectionAsync(
        Guid connectionId,
        string name,
        string settingsJson,
        string? username = null,
        string? password = null,
        string? domain = null)
    {
        var connection = await GetManagedConnectionAsync(connectionId);
        if (_providerCatalog is null)
            throw new NotSupportedException(R("Web_ExternalStorage_ProviderUnsupported"));
        var provider = _providerCatalog.GetRequired(connection.ProviderId);
        if (connection.AuthorizationMode is not (StorageAuthorizationMode.HostMount
            or StorageAuthorizationMode.SshKey
            or StorageAuthorizationMode.UsernamePassword
            or StorageAuthorizationMode.NetworkIdentity))
            throw new NotSupportedException(R("Web_ExternalStorage_ProviderUnsupported"));
        var normalizedName = name.Trim();
        if (string.IsNullOrWhiteSpace(normalizedName) || normalizedName.Length > 200)
            throw new ArgumentException(R("Web_CloudAccess_InvalidConnectionName"));
        if (string.IsNullOrWhiteSpace(settingsJson) || settingsJson.Length > 64 * 1024)
            throw new ArgumentException(R("Web_ExternalStorage_InvalidSettings"));

        connection.Name = normalizedName;
        connection.SettingsJson = settingsJson;

        if (connection.AuthorizationMode == StorageAuthorizationMode.UsernamePassword)
        {
            if (_credentialVault is null)
                throw new ArgumentException(R("Web_ExternalStorage_InvalidCredentials"));
            var existing = string.IsNullOrWhiteSpace(connection.EncryptedCredentialPayload)
                ? new Dictionary<string, string>(StringComparer.Ordinal)
                : _credentialVault.UnprotectConnectionCredentials(connection);
            var resolvedUsername = string.IsNullOrWhiteSpace(username)
                ? existing.GetValueOrDefault("username") ?? string.Empty
                : username.Trim();
            var resolvedPassword = string.IsNullOrEmpty(password)
                ? existing.GetValueOrDefault("password") ?? string.Empty
                : password;
            if (string.IsNullOrWhiteSpace(resolvedUsername) || string.IsNullOrEmpty(resolvedPassword))
                throw new ArgumentException(R("Web_ExternalStorage_InvalidCredentials"));
            var credentials = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["username"] = resolvedUsername,
                ["password"] = resolvedPassword
            };
            if (!string.IsNullOrWhiteSpace(domain))
                credentials["domain"] = domain.Trim();
            connection.EncryptedCredentialPayload = _credentialVault.ProtectConnectionCredentials(connection, credentials);
            connection.AccountDisplayName = resolvedUsername;
            connection.CredentialUpdatedAtUtc = DateTime.UtcNow;
        }

        var health = await provider.TestAsync(connection);
        connection.State = MapUnhealthyState(connection, health);
        connection.LastVerifiedAtUtc = health.CheckedAtUtc;
        connection.LastErrorCode = health.IsHealthy ? null : health.Code;
        await _connections.SaveAsync(connection);
        await LoadAsync();
    }

    /// <summary>
    /// Returns the stored username and domain for prefilling the connection edit
    /// form. The password is never returned; leaving it blank on save keeps it.
    /// </summary>
    public async Task<(string? Username, string? Domain)> GetConnectionCredentialFieldsAsync(Guid connectionId)
    {
        var connection = await GetManagedConnectionAsync(connectionId);
        if (_credentialVault is null || string.IsNullOrWhiteSpace(connection.EncryptedCredentialPayload))
            return (connection.AccountDisplayName, null);
        try
        {
            var credentials = _credentialVault.UnprotectConnectionCredentials(connection);
            return (credentials.GetValueOrDefault("username") ?? connection.AccountDisplayName,
                    credentials.GetValueOrDefault("domain"));
        }
        catch
        {
            return (connection.AccountDisplayName, null);
        }
    }

    /// <summary>Disables or re-enables a connection without removing its consumers.</summary>
    public async Task SetConnectionEnabledAsync(Guid connectionId, bool enabled)
    {
        var connection = await GetManagedConnectionAsync(connectionId);
        connection.State = enabled
            ? RequiresProtectedCredential(connection.AuthorizationMode)
              && string.IsNullOrWhiteSpace(connection.EncryptedCredentialPayload)
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
            await ListRemoteFoldersAsync(connectionId, "/");
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

    public async Task<List<CloudDirectoryItem>> ListRemoteFoldersAsync(Guid connectionId, string path)
    {
        var connectionRecord = await GetUsableConnectionAsync(connectionId);
        if (connectionRecord.State != StorageConnectionState.Ready)
            throw new InvalidOperationException(R("Web_CloudAccess_ConnectionNotReady"));
        if (_directoryTargets is not null)
            return (await _directoryTargets.ListDirectoriesAsync(connectionRecord, path))
                .Select(item => new CloudDirectoryItem(item.Name, NormalizeRemotePath(item.Path)))
                .ToList();

        // Compatibility path for isolated legacy hosts without the provider catalog.
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
        // The creator and the built-in Admins group receive access to every new share
        // (see CreateShareAsync). They must always be visible and manageable in the ACL
        // editor even when they sit outside this department's scope.
        if (_actor is not null) users[_actor.User.Id] = _actor.User;
        foreach (var group in await _groups.GetAllAsync())
            if (string.Equals(group.Name, AdminsGroupName, StringComparison.OrdinalIgnoreCase))
                groups[group.Id] = group;
        AvailablePrincipals = users.Values.Cast<Identity>().Concat(groups.Values)
            .OrderBy(x => x is Group ? 1 : 0).ThenBy(x => x.Name).ToList();
    }

    public async Task CreateShareAsync(
        Guid connectionId,
        string name,
        string remoteRootPath,
        Guid departmentId,
        IReadOnlyDictionary<Guid, CloudAccessPermission> grants)
    {
        await EnsureCanManageDepartmentAsync(departmentId);
        var connectionRecord = await GetUsableConnectionAsync(connectionId);
        if (_providerCatalog is not null
            && !_providerCatalog.GetRequired(connectionRecord.ProviderId).Capabilities
                .HasFlag(StorageProviderCapabilities.Browse))
            throw new NotSupportedException(R("Web_ExternalStorage_VirtualShareUnsupported"));
        if (connectionRecord.State != StorageConnectionState.Ready)
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

        StorageDirectoryTarget folder;
        if (_directoryTargets is not null)
            folder = await _directoryTargets.ResolveDirectoryAsync(connectionRecord, root);
        else
        {
            await using var legacyConnection = _oneDriveConnections.Create(connectionRecord);
            var legacyFolder = await legacyConnection.ResolveFolderAsync(root);
            folder = new StorageDirectoryTarget(NormalizeRemotePath(legacyFolder.Path), legacyFolder.ProviderId);
        }

        var share = new CloudAccessShare
        {
            ConnectionId = connectionId,
            DepartmentId = departmentId,
            Name = normalizedName,
            RemoteRootPath = folder.Path,
            RemoteRootItemId = folder.StableId,
            IsEnabled = true
        };
        await _repository.UpsertShareAsync(share);

        // The creator and the built-in Admins group always get read+write on a new share,
        // regardless of the department-scoped principal picker.
        var effectiveGrants = new Dictionary<Guid, CloudAccessPermission>(grants)
        {
            [_actor!.User.Id] = CloudAccessPermission.Write
        };
        var alwaysAllowed = new HashSet<Guid> { _actor.User.Id };
        if (await ResolveAdminsGroupIdAsync() is { } adminsGroupId)
        {
            effectiveGrants[adminsGroupId] = CloudAccessPermission.Write;
            alwaysAllowed.Add(adminsGroupId);
        }
        await _repository.ReplaceGrantsAsync(
            share.Id, BuildGrants(share.Id, connectionRecord, effectiveGrants, alwaysAllowed));
        await LoadAsync();
    }

    /// <summary>Name of the seeded system group that always receives access to new virtual shares.</summary>
    private const string AdminsGroupName = "Admins";

    private async Task<Guid?> ResolveAdminsGroupIdAsync()
        => (await _groups.GetAllAsync())
            .FirstOrDefault(group => string.Equals(group.Name, AdminsGroupName, StringComparison.OrdinalIgnoreCase))?.Id;

    /// <summary>
    /// Materializes root-level grants for the allowed principals, clamping Write to Read
    /// when the backing provider cannot write (mirrors the old share-wide read-only flag).
    /// Principals in <paramref name="alwaysAllowed"/> bypass the department-scoped filter.
    /// </summary>
    private IEnumerable<CloudAccessGrant> BuildGrants(
        Guid shareId,
        StorageConnection connection,
        IReadOnlyDictionary<Guid, CloudAccessPermission> grants,
        IReadOnlySet<Guid>? alwaysAllowed = null)
    {
        bool canWrite = Supports(connection, StorageProviderCapabilities.Write);
        var allowed = AvailablePrincipals.Select(x => x.Id).ToHashSet();
        return grants
            .Where(pair => allowed.Contains(pair.Key) || (alwaysAllowed?.Contains(pair.Key) ?? false))
            .Select(pair => new CloudAccessGrant
            {
                ShareId = shareId,
                PrincipalId = pair.Key,
                Permission = canWrite ? pair.Value : CloudAccessPermission.Read,
                GrantedByUserId = _actor!.User.Id
            });
    }

    /// <summary>
    /// Updates a virtual share while preserving its identity and existing grants.
    /// The selected remote folder is resolved again to keep the persisted provider item ID current.
    /// </summary>
    public async Task UpdateShareAsync(
        Guid shareId,
        string name,
        string remoteRootPath)
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
        if (connectionRecord.State != StorageConnectionState.Ready)
            throw new InvalidOperationException(R("Web_CloudAccess_ConnectionNotReady"));

        StorageDirectoryTarget folder;
        if (_directoryTargets is not null)
            folder = await _directoryTargets.ResolveDirectoryAsync(connectionRecord, root);
        else
        {
            await using var legacyConnection = _oneDriveConnections.Create(connectionRecord);
            var legacyFolder = await legacyConnection.ResolveFolderAsync(root);
            folder = new StorageDirectoryTarget(NormalizeRemotePath(legacyFolder.Path), legacyFolder.ProviderId);
        }

        share.Name = normalizedName;
        share.RemoteRootPath = folder.Path;
        share.RemoteRootItemId = folder.StableId;
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

    public async Task<Dictionary<Guid, CloudAccessPermission>> LoadShareGrantsAsync(Guid shareId)
    {
        var share = await _repository.GetShareAsync(shareId)
                    ?? throw new InvalidOperationException(R("Web_CloudAccess_ShareMissing"));
        await EnsureCanManageDepartmentAsync(share.DepartmentId);
        await LoadPrincipalsAsync(share.DepartmentId);
        var allowed = AvailablePrincipals.Select(x => x.Id).ToHashSet();
        return (await _repository.GetGrantsAsync(shareId))
            .Where(grant => allowed.Contains(grant.PrincipalId))
            .ToDictionary(grant => grant.PrincipalId, grant => grant.Permission);
    }

    public async Task UpdateShareGrantsAsync(Guid shareId, IReadOnlyDictionary<Guid, CloudAccessPermission> grants)
    {
        var share = await _repository.GetShareAsync(shareId)
                    ?? throw new InvalidOperationException(R("Web_CloudAccess_ShareMissing"));
        await EnsureCanManageDepartmentAsync(share.DepartmentId);
        await LoadPrincipalsAsync(share.DepartmentId);
        var connectionRecord = await GetUsableConnectionAsync(share.ConnectionId);
        // The editor only manages principals it can display (the department scope). Grants
        // for principals outside that scope — e.g. the creator or the Admins group on a
        // non-global share — are preserved so an ACL edit never silently drops them.
        var allowed = AvailablePrincipals.Select(x => x.Id).ToHashSet();
        var preserved = (await _repository.GetGrantsAsync(shareId))
            .Where(grant => !allowed.Contains(grant.PrincipalId));
        await _repository.ReplaceGrantsAsync(
            shareId, preserved.Concat(BuildGrants(shareId, connectionRecord, grants)));
    }

    public async Task DeleteConnectionAsync(Guid connectionId)
    {
        var connection = await GetManagedConnectionAsync(connectionId);
        var result = await _connections.DeleteAsync(connection.Id);
        if (result == StorageConnectionDeleteResult.InUse)
            throw new InvalidOperationException(R("Web_StorageConnection_DeleteInUse"));
        if (result == StorageConnectionDeleteResult.Deleted
            && _providerCatalog?.TryGet(connection.ProviderId, out var provider) == true)
            await provider.RevokeAsync(connection);
        await LoadAsync();
    }

    private async Task<StorageConnection> GetManagedConnectionAsync(Guid connectionId)
    {
        var connection = await _connections.GetAsync(connectionId)
                         ?? throw new InvalidOperationException(R("Web_CloudAccess_ConnectionMissing"));
        await EnsureCanManageConnectionsAsync();
        return connection;
    }

    private async Task<StorageConnection> GetUsableConnectionAsync(Guid connectionId)
    {
        var connection = await _connections.GetAsync(connectionId)
                         ?? throw new InvalidOperationException(R("Web_CloudAccess_ConnectionMissing"));
        await EnsureCanUseConnectionsAsync();
        return connection;
    }

    private async Task EnsureCanManageConnectionsAsync()
    {
        _actor ??= await GetActorAsync();
        if (_actor is null || !await _managementAuth.HasGlobalPermissionAsync(
                _actor, ManagementPermission.ManageConnections))
            throw new UnauthorizedAccessException(R("Web_StorageConnection_ManageDenied"));
    }

    private async Task EnsureCanUseConnectionsAsync()
    {
        _actor ??= await GetActorAsync();
        if (_actor is null || !await _managementAuth.HasGlobalPermissionAsync(
                _actor, ManagementPermission.UseConnections))
            throw new UnauthorizedAccessException(R("Web_StorageConnection_UseDenied"));
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

    public bool Supports(StorageConnection connection, StorageProviderCapabilities capability)
    {
        if (_providerCatalog is null) return true;
        return _providerCatalog.TryGet(connection.ProviderId, out var provider)
               && provider.Capabilities.HasFlag(capability);
    }

    public string GetProviderName(string providerId)
        => _providerCatalog?.TryGet(providerId, out var provider) == true
            ? provider.DisplayName
            : providerId;

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

    private static bool RequiresProtectedCredential(StorageAuthorizationMode mode)
        => mode is not (StorageAuthorizationMode.HostMount
            or StorageAuthorizationMode.SshKey
            or StorageAuthorizationMode.NetworkIdentity);

    private static HashSet<Guid> GetDepartmentIds(
        AuthorizedScopeResult scope,
        IReadOnlyCollection<Department> allDepartments)
        => scope.IsUnrestricted
            ? allDepartments.Select(department => department.Id).ToHashSet()
            : scope.ScopeIds.ToHashSet();

    private static string NormalizeRemotePath(string? path)
    {
        string normalized = (path ?? string.Empty).Replace('\\', '/').Trim('/');
        return normalized.Length == 0 ? "/" : $"/{normalized}";
    }

    private static string R(string key) => Resources.ResourceManager.GetString(key) ?? key;
}

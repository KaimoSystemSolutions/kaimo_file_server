using Kaimo_File_Server.Core.Domain;
using Kaimo_File_Server.Core.Domain.Department;
using Kaimo_File_Server.Core.Domain.Identity;
using Kaimo_File_Server.Core.Language;
using Kaimo_File_Server.Core.Repositories;
using Kaimo_File_Server.Core.Security;
using Kaimo_File_Server.Core.Services;
using Kaimo_File_Server.Core.Services.ExternalStorage;
using Kaimo_File_Server.Web.DynamicHelpers;
using Kaimo_File_Server.Web.Services;
using Microsoft.AspNetCore.Components.Authorization;

namespace Kaimo_File_Server.Web.Components.ViewModels;

public sealed partial class CloudAccessViewModel
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
    // DepartmentId → department (all departments), for the list-view chip lookup.
    private IReadOnlyDictionary<Guid, Department> _departmentsById =
        new Dictionary<Guid, Department>();

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

    /// <summary>
    /// The department a virtual share belongs to, with its palette colors for the
    /// list-view chip. Mirrors the local share overview so both lists render the same
    /// chip. Returns <c>null</c> when the department is unknown.
    /// </summary>
    public DepartmentDisplay? GetDepartmentDisplay(CloudAccessShare share)
    {
        if (!_departmentsById.TryGetValue(share.DepartmentId, out var department))
            return null;

        var (color, soft) = DepartmentPalette.For(department.Id, department.Color);
        return new DepartmentDisplay(
            string.IsNullOrWhiteSpace(department.Name) ? "—" : department.Name, color, soft);
    }

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
            _departmentsById = allDepartments.ToDictionary(x => x.Id);
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

    /// <summary>
    /// Connection names are unique (database index). Checking up front turns the otherwise
    /// generic save failure into a clear message and skips the provider test for a name
    /// that could never be saved. Case-insensitive, like share names, to avoid look-alikes.
    /// An unchanged name of <paramref name="current"/> is always accepted: older data may
    /// hold names that differ only in case (the index is case-sensitive), and editing such a
    /// connection's settings must not force a rename.
    /// </summary>
    private async Task EnsureConnectionNameAvailableAsync(string name, StorageConnection? current = null)
    {
        if (current is not null && string.Equals(current.Name, name, StringComparison.Ordinal))
            return;
        if ((await _connections.GetAllAsync()).Any(x => x.Id != current?.Id
                && string.Equals(x.Name, name, StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException(R("Web_ExternalStorage_ConnectionNameExists"));
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

/// <summary>
/// Parameters for launching a provider's interactive authorization page. The
/// ticket is a single-use secret, so it is POSTed in a form body rather than
/// placed in the navigation URL where it would land in logs and history.
/// </summary>
public sealed record InteractiveAuthorization(string Endpoint, Guid ConnectionId, string Ticket);

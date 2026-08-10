using Kaimo_File_Server.Core.Domain;
using Kaimo_File_Server.Core.Domain.Department;
using Kaimo_File_Server.Core.Domain.Identity;
using Kaimo_File_Server.Core.Helpers;
using Kaimo_File_Server.Core.Language;
using Kaimo_File_Server.Core.Repositories;
using Kaimo_File_Server.Core.Security;
using Kaimo_File_Server.Core.Services;
using Kaimo_File_Server.Infrastructure.Clouds;
using Kaimo_File_Server.Web.Services;
using Microsoft.AspNetCore.Components.Authorization;

namespace Kaimo_File_Server.Web.Components.ViewModels;

public sealed class CloudAccessViewModel
{
    private readonly ICloudAccessRepository _repository;
    private readonly ICloudAccessCredentialProtector _protector;
    private readonly ICloudAuthorizationTicketStore _tickets;
    private readonly IManagementAuthService _managementAuth;
    private readonly IUserContextFactory _userContextFactory;
    private readonly AuthenticationStateProvider _authenticationState;
    private readonly IDepartmentRepository _departments;
    private readonly IUserRepository _users;
    private readonly IGroupRepository _groups;
    private readonly IShareRepository _localShares;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<CloudAccessViewModel> _logger;
    private UserContext? _actor;

    public CloudAccessViewModel(
        ICloudAccessRepository repository,
        ICloudAccessCredentialProtector protector,
        ICloudAuthorizationTicketStore tickets,
        IManagementAuthService managementAuth,
        IUserContextFactory userContextFactory,
        AuthenticationStateProvider authenticationState,
        IDepartmentRepository departments,
        IUserRepository users,
        IGroupRepository groups,
        IShareRepository localShares,
        IHttpClientFactory httpClientFactory,
        ILogger<CloudAccessViewModel> logger)
    {
        _repository = repository;
        _protector = protector;
        _tickets = tickets;
        _managementAuth = managementAuth;
        _userContextFactory = userContextFactory;
        _authenticationState = authenticationState;
        _departments = departments;
        _users = users;
        _groups = groups;
        _localShares = localShares;
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    public List<CloudAccessConnection> Connections { get; private set; } = [];
    public List<CloudAccessShare> Shares { get; private set; } = [];
    public List<Department> ManageableDepartments { get; private set; } = [];
    public List<Identity> AvailablePrincipals { get; private set; } = [];
    public bool IsLoading { get; private set; }
    public bool CanManage { get; private set; }
    public string? ErrorMessage { get; private set; }

    public async Task LoadAsync()
    {
        IsLoading = true;
        ErrorMessage = null;
        try
        {
            _actor = await GetActorAsync();
            if (_actor is null) return;
            var scope = await _managementAuth.GetAuthorizedDepartmentIdsAsync(
                _actor, ManagementPermission.ManageCloudAccess);
            CanManage = scope.IsUnrestricted || scope.ScopeIds.Count > 0;
            var allDepartments = await _departments.GetAllAsync();
            ManageableDepartments = scope.IsUnrestricted
                ? allDepartments.OrderBy(x => x.Name).ToList()
                : allDepartments.Where(x => scope.ScopeIds.Contains(x.Id)).OrderBy(x => x.Name).ToList();
            var departmentIds = ManageableDepartments.Select(x => x.Id).ToHashSet();
            Connections = (await _repository.GetConnectionsAsync())
                .Where(x => departmentIds.Contains(x.DepartmentId)).ToList();
            Shares = (await _repository.GetSharesAsync())
                .Where(x => departmentIds.Contains(x.DepartmentId)).ToList();
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
        await EnsureCanManageDepartmentAsync(departmentId);
        if (string.IsNullOrWhiteSpace(name) || name.Trim().Length > 200)
            throw new ArgumentException(R("Web_CloudAccess_InvalidConnectionName"));
        var connection = new CloudAccessConnection
        {
            DepartmentId = departmentId,
            CreatedByUserId = _actor!.User.Id,
            Provider = "onedrive",
            Name = name.Trim(),
            State = CloudAccessConnectionState.PendingAuthorization
        };
        await _repository.UpsertConnectionAsync(connection);
        var ticket = _tickets.Issue(connection.Id, string.Empty, "onedrive-access");
        return $"/api/cloud-access/onedrive/connect?connectionId={connection.Id}&ticket={Uri.EscapeDataString(ticket)}";
    }

    public async Task<string> AuthorizeConnectionAsync(Guid connectionId)
    {
        var connection = await GetManagedConnectionAsync(connectionId);
        var ticket = _tickets.Issue(connection.Id, string.Empty, "onedrive-access");
        return $"/api/cloud-access/onedrive/connect?connectionId={connection.Id}&ticket={Uri.EscapeDataString(ticket)}";
    }

    public async Task<List<CloudDirectoryItem>> ListOneDriveFoldersAsync(Guid connectionId, string path)
    {
        var connectionRecord = await GetManagedConnectionAsync(connectionId);
        if (connectionRecord.State != CloudAccessConnectionState.Ready
            || string.IsNullOrWhiteSpace(connectionRecord.ProtectedCredentials))
            throw new InvalidOperationException(R("Web_CloudAccess_ConnectionNotReady"));
        if (!ShareRelativePath.TryNormalizeStrict(path.Trim('/'), out var normalized))
            throw new UnauthorizedAccessException(R("Web_CloudAccess_InvalidRemotePath"));
        var credentials = _protector.Unprotect(connectionRecord.ProtectedCredentials);
        await using var connection = new OneDriveConnection(
            credentials, _httpClientFactory.CreateClient("CloudAccessOneDrive"));
        var items = await connection.ListDetailedAsync(normalized);
        await PersistCredentialsIfRotatedAsync(connectionRecord, connection, credentials);
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
        var connectionRecord = await GetManagedConnectionAsync(connectionId);
        if (connectionRecord.State != CloudAccessConnectionState.Ready
            || string.IsNullOrWhiteSpace(connectionRecord.ProtectedCredentials))
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

        var credentials = _protector.Unprotect(connectionRecord.ProtectedCredentials);
        await using var connection = new OneDriveConnection(
            credentials, _httpClientFactory.CreateClient("CloudAccessOneDrive"));
        var folder = await connection.ResolveFolderAsync(root);
        await PersistCredentialsIfRotatedAsync(connectionRecord, connection, credentials);

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
        await _repository.DeleteConnectionAsync(connection.Id);
        await LoadAsync();
    }

    private async Task<CloudAccessConnection> GetManagedConnectionAsync(Guid connectionId)
    {
        var connection = await _repository.GetConnectionAsync(connectionId)
                         ?? throw new InvalidOperationException(R("Web_CloudAccess_ConnectionMissing"));
        await EnsureCanManageDepartmentAsync(connection.DepartmentId);
        return connection;
    }

    private async Task EnsureCanManageDepartmentAsync(Guid departmentId)
    {
        _actor ??= await GetActorAsync();
        if (_actor is null || !await _managementAuth.CanManageDepartmentAsync(
                _actor, departmentId, ManagementPermission.ManageCloudAccess))
            throw new UnauthorizedAccessException(R("Web_CloudAccess_DepartmentDenied"));
    }

    private async Task<bool> IsShareNameAvailableAsync(string name)
    {
        var comparer = StringComparer.OrdinalIgnoreCase;
        if ((await _repository.GetSharesAsync()).Any(x => comparer.Equals(x.Name, name)))
            return false;

        return !(await _localShares.GetAllAsync()).Any(x => comparer.Equals(x.Name, name));
    }

    private async Task PersistCredentialsIfRotatedAsync(
        CloudAccessConnection record, OneDriveConnection connection, Dictionary<string, string> credentials)
    {
        if (!connection.HasPendingCredentialChanges) return;
        await _repository.UpdateConnectionRuntimeAsync(
            record.Id, _protector.Protect(credentials), null, null,
            CloudAccessConnectionState.Ready, null);
        connection.AcknowledgeCredentialChanges();
    }

    private async Task<UserContext?> GetActorAsync()
    {
        var state = await _authenticationState.GetAuthenticationStateAsync();
        var username = state.User.Identity?.Name;
        return string.IsNullOrWhiteSpace(username) ? null : await _userContextFactory.CreateByUsernameAsync(username);
    }

    private static string R(string key) => Resources.ResourceManager.GetString(key) ?? key;
}

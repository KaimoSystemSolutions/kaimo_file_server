using Kaimo_File_Server.Core.Domain;
using Kaimo_File_Server.Core.Domain.Department;
using Kaimo_File_Server.Core.Repositories;
using Kaimo_File_Server.Core.Security;
using Kaimo_File_Server.Core.Services;
using Kaimo_File_Server.Core.Services.ExternalStorage;
using Kaimo_File_Server.Web.DynamicHelpers;
using Kaimo_File_Server.Web.Services;
using Microsoft.AspNetCore.Components.Authorization;

namespace Kaimo_File_Server.Web.Components.ViewModels;

public sealed class CloudAccessShareBrowserViewModel(
    IStorageConnectionRepository connectionsRepository,
    CloudAccessAuthorizationService authorization,
    IUserContextFactory userContextFactory,
    AuthenticationStateProvider authenticationState,
    IStorageConnectionProviderCatalog providerCatalog,
    IManagementAuthService managementAuth,
    IDepartmentRepository departmentRepository,
    ILogger<CloudAccessShareBrowserViewModel> logger)
{
    public List<CloudAccessShareListItem> Shares { get; private set; } = [];
    public bool IsLoading { get; private set; }

    /// <summary>
    /// Whether the actor may see the department column in the share overview.
    /// Gated by the department view/edit management rights (any scope).
    /// </summary>
    public bool CanViewDepartmentColumn { get; private set; }

    public async Task LoadAsync()
    {
        IsLoading = true;
        try
        {
            var state = await authenticationState.GetAuthenticationStateAsync();
            var username = state.User.Identity?.Name;
            var actor = string.IsNullOrWhiteSpace(username) ? null : await userContextFactory.CreateByUsernameAsync(username);
            if (actor is null) { CanViewDepartmentColumn = false; Shares = []; return; }

            // Department column: visible only to actors holding department view or
            // edit rights (any scope). Names are resolved for every department so a
            // share homed outside the actor's manage scope still shows its name.
            CanViewDepartmentColumn = await managementAuth.HasAnyPermissionAsync(
                actor, ManagementPermission.ViewDepartment | ManagementPermission.EditDepartment);
            var departments = CanViewDepartmentColumn
                ? (await departmentRepository.GetAllAsync()).ToDictionary(department => department.Id)
                : new Dictionary<Guid, Department>();

            var visible = await authorization.GetVisibleSharesAsync(actor);
            var connections = (await connectionsRepository.GetAllAsync()).ToDictionary(x => x.Id);
            var usable = visible.Where(x => connections.TryGetValue(x.ConnectionId, out var connection)
                                            && connection.State == StorageConnectionState.Ready);
            var items = new List<CloudAccessShareListItem>();
            foreach (var x in usable)
            {
                // Read-only for THIS actor: their highest grant is not Write.
                bool readOnly = await authorization.GetEffectivePermissionAsync(actor, x)
                    != CloudAccessPermission.Write;
                items.Add(new CloudAccessShareListItem(
                    x.Id, x.Name, connections[x.ConnectionId].ProviderId,
                    ProviderDisplayName(connections[x.ConnectionId].ProviderId),
                    connections[x.ConnectionId].Name,
                    x.RemoteRootPath, readOnly,
                    ResolveDepartment(departments, x.DepartmentId)));
            }
            Shares = items.OrderBy(x => x.Name).ToList();
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Unable to list visible Cloud Access shares");
            Shares = [];
        }
        finally { IsLoading = false; }
    }

    private string ProviderDisplayName(string providerId)
        => providerCatalog.TryGet(providerId, out var provider) ? provider.DisplayName : providerId;

    private static DepartmentDisplay? ResolveDepartment(
        IReadOnlyDictionary<Guid, Department> departments, Guid departmentId)
    {
        if (!departments.TryGetValue(departmentId, out var department))
            return null;

        var (color, soft) = DepartmentPalette.For(department.Id, department.Color);
        return new DepartmentDisplay(
            string.IsNullOrWhiteSpace(department.Name) ? "—" : department.Name, color, soft);
    }
}

public sealed record CloudAccessShareListItem(
    Guid Id, string Name, string Provider, string ProviderDisplayName, string ConnectionName, string RemotePath, bool IsReadOnly, DepartmentDisplay? Department);

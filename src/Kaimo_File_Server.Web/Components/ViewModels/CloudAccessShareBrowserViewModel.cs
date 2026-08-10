using Kaimo_File_Server.Core.Domain;
using Kaimo_File_Server.Core.Repositories;
using Kaimo_File_Server.Web.Services;
using Microsoft.AspNetCore.Components.Authorization;

namespace Kaimo_File_Server.Web.Components.ViewModels;

public sealed class CloudAccessShareBrowserViewModel(
    ICloudAccessRepository repository,
    CloudAccessAuthorizationService authorization,
    IUserContextFactory userContextFactory,
    AuthenticationStateProvider authenticationState,
    ILogger<CloudAccessShareBrowserViewModel> logger)
{
    public List<CloudAccessShareListItem> Shares { get; private set; } = [];
    public bool IsLoading { get; private set; }

    public async Task LoadAsync()
    {
        IsLoading = true;
        try
        {
            var state = await authenticationState.GetAuthenticationStateAsync();
            var username = state.User.Identity?.Name;
            var actor = string.IsNullOrWhiteSpace(username) ? null : await userContextFactory.CreateByUsernameAsync(username);
            if (actor is null) { Shares = []; return; }
            var visible = await authorization.GetVisibleSharesAsync(actor);
            var connections = (await repository.GetConnectionsAsync()).ToDictionary(x => x.Id);
            Shares = visible.Where(x => connections.TryGetValue(x.ConnectionId, out var connection)
                                        && connection.State == CloudAccessConnectionState.Ready)
                .Select(x => new CloudAccessShareListItem(
                    x.Id, x.Name, connections[x.ConnectionId].Provider, x.RemoteRootPath, x.IsReadOnly))
                .OrderBy(x => x.Name).ToList();
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Unable to list visible Cloud Access shares");
            Shares = [];
        }
        finally { IsLoading = false; }
    }
}

public sealed record CloudAccessShareListItem(Guid Id, string Name, string Provider, string RemotePath, bool IsReadOnly);

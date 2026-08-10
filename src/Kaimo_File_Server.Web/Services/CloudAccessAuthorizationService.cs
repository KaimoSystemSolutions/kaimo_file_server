using Kaimo_File_Server.Core.Domain;
using Kaimo_File_Server.Core.Domain.Identity;
using Kaimo_File_Server.Core.Repositories;
using Kaimo_File_Server.Core.Security;
using Kaimo_File_Server.Core.Services;

namespace Kaimo_File_Server.Web.Services;

public sealed class CloudAccessAuthorizationService(
    ICloudAccessRepository repository,
    IManagementAuthService managementAuth)
{
    public async Task<bool> CanManageAsync(UserContext actor, CloudAccessShare share)
    {
        var scope = await managementAuth.GetAuthorizedDepartmentIdsAsync(
            actor, ManagementPermission.ManageCloudAccess);
        return scope.IsUnrestricted || scope.ScopeIds.Contains(share.DepartmentId);
    }

    public async Task<bool> CanAccessAsync(UserContext actor, CloudAccessShare share)
    {
        if (!share.IsEnabled) return false;
        if (await CanManageAsync(actor, share)) return true;
        var principals = actor.Groups.Select(x => x.Id).Append(actor.User.Id);
        return await repository.HasGrantAsync(share.Id, principals);
    }

    public async Task<List<CloudAccessShare>> GetVisibleSharesAsync(UserContext actor)
    {
        var shares = await repository.GetSharesAsync();
        var visible = new List<CloudAccessShare>();
        foreach (var share in shares)
            if (await CanAccessAsync(actor, share)) visible.Add(share);
        return visible;
    }
}

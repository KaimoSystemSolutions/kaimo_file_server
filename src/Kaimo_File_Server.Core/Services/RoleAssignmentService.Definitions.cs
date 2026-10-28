using Kaimo_File_Server.Core.Domain.Identity;
using Kaimo_File_Server.Core.Language;
using Kaimo_File_Server.Core.Security;

namespace Kaimo_File_Server.Core.Services;

/// <summary>
/// Changes to role definitions. A role is a global object: changing or deleting it changes every
/// assignment of it, so the actor needs global <see cref="ManagementPermission.AssignRoles"/> and
/// may only grant or revoke bits it holds system-wide itself, and the last system-wide
/// administrator never loses that status through a role edit.
/// </summary>
public sealed partial class RoleAssignmentService
{
    private static string SystemRoleUndeletableMessage
        => Resources.ResourceManager.GetString("Web_Error_SystemRoleUndeletable")
           ?? "System roles cannot be deleted.";

    /// <summary>
    /// Validates changing <paramref name="role"/>'s permissions to <paramref name="newPermissions"/>:
    /// the new set and every removed bit must lie within the actor's global permissions, and
    /// demoting an admin role must leave another enabled system-wide administrator.
    /// </summary>
    /// <returns>A localized error message, or <c>null</c> when the change is allowed.</returns>
    public async Task<string?> AuthorizeRoleChangeAsync(
        UserContext actor, Role role, ManagementPermission newPermissions)
    {
        var removed = role.ManagementPermissions & ~newPermissions;
        if (!await HoldsGloballyAsync(actor, newPermissions | removed))
            return Resources.Web_Error_NoPermission;

        var demotesAdmin = IsAdminRole(role)
            && (newPermissions & ManagementPermission.FullAdmin) != ManagementPermission.FullAdmin;
        return demotesAdmin && await RemovesLastAdminAsync(role.Id) ? Resources.Web_Error_LastAdmin : null;
    }

    /// <summary>
    /// Deletes a custom role together with its assignments. System roles are never deleted; the
    /// actor must hold every bit of the role globally, and the last administrator is protected.
    /// </summary>
    /// <returns>A localized error message, or <c>null</c> on success.</returns>
    public async Task<string?> DeleteRoleAsync(UserContext actor, Role role)
    {
        if (role.IsSystemRole)
            return SystemRoleUndeletableMessage;
        if (!await HoldsGloballyAsync(actor, role.ManagementPermissions))
            return Resources.Web_Error_NoPermission;
        if (IsAdminRole(role) && await RemovesLastAdminAsync(role.Id))
            return Resources.Web_Error_LastAdmin;

        // Assignments carry no foreign key to the role, so they would otherwise stay behind.
        foreach (var assignment in await assignmentRepo.GetByRoleAsync(role.Id))
            await assignmentRepo.DeleteAsync(assignment.Id);
        await roleRepo.DeleteAsync(role.Id);
        return null;
    }

    // Global AssignRoles plus every bit of the mask, held system-wide.
    private async Task<bool> HoldsGloballyAsync(UserContext actor, ManagementPermission mask)
    {
        var global = await mgmtAuth.GetEffectivePermissionsAtAsync(actor, ScopeType.Global, Guid.Empty);
        return (global & ManagementPermission.AssignRoles) != 0
               && (mask & ~global) == ManagementPermission.None;
    }

    // True when the role is the last source of system-wide admin status for every enabled admin.
    private async Task<bool> RemovesLastAdminAsync(Guid roleId)
    {
        var withoutRole = await ManagementAuthService.GetGlobalPermissionHolderUserIdsAsync(
            assignmentRepo, roleRepo, userRepo, groupRepo, r => IsAdminRole(r) && r.Id != roleId);
        return withoutRole.Count == 0 && (await GetEnabledGlobalAdminUserIdsAsync()).Count > 0;
    }
}

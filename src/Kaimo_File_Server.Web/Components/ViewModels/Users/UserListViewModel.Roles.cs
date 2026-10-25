using Kaimo_File_Server.Core.Domain.Identity;
using Kaimo_File_Server.Core.Security;
using Kaimo_File_Server.Core.Language;

namespace Kaimo_File_Server.Web.Components.ViewModels;

public partial class UserListViewModel
{
    // -- Role-Details --
    public List<ScopedAssignmentDisplayItem> RoleScopedAssignments { get; private set; } = [];

    // -- Role-Edit --
    public string EditRoleName { get; set; } = "";
    public ManagementPermission EditRolePermissions { get; set; } = ManagementPermission.None;

    // -- Create Role --
    public bool IsCreatingRole { get; set; }
    public string CreateRoleName { get; set; } = "";

    // ══════════════════════════════════════════
    //  Permission Helpers (Role-Edit UI)
    // ══════════════════════════════════════════

    public bool HasEditPermission(ManagementPermission flag)
        => (EditRolePermissions & flag) == flag;

    public void ToggleEditPermission(ManagementPermission flag, bool value)
    {
        if (value) EditRolePermissions |= flag;
        else EditRolePermissions &= ~flag;
    }

    // Built on each access so the labels resolve against the current UI culture
    // (CurrentUICulture is set per request, so a static-readonly list would freeze
    // the language captured at type-load time).
    public static List<PermissionGroup> PermissionGroups =>
    [
        new(Resources.Web_PermGroup_UserMgmt,
        [
            new(ManagementPermission.CreateUsers, Resources.Web_Perm_CreateUsers),
            new(ManagementPermission.DeleteUsers, Resources.Web_Perm_DeleteUsers),
            new(ManagementPermission.EditUserProfiles, Resources.Web_Perm_EditProfiles),
            new(ManagementPermission.ResetPasswords, Resources.Web_Perm_ResetPasswords),
            new(ManagementPermission.ManageHomes,
                Resources.ResourceManager.GetString("Web_Perm_ManageHomes") ?? "Manage home folders"),
            new(ManagementPermission.AccessHomeContents,
                Resources.ResourceManager.GetString("Web_Perm_AccessHomeContents") ?? "Access home folder contents"),
            // EnableDisableUsers is intentionally NOT offered here: the IsEnabled
            // toggle lives in the profile-edit form and is enforced via
            // EditUserProfiles. A standalone bit would be dead (never checked).
        ]),
        new(Resources.Web_PermGroup_GroupMgmt,
        [
            new(ManagementPermission.CreateGroups, Resources.Web_Perm_CreateGroups),
            new(ManagementPermission.DeleteGroups, Resources.Web_Perm_DeleteGroups),
            new(ManagementPermission.ManageGroupMembers, Resources.Web_Perm_ManageMembers),
        ]),
        new(Resources.Web_PermGroup_AssignDelegation,
        [
            new(ManagementPermission.AssignGroups, Resources.Web_Perm_AssignGroups),
            new(ManagementPermission.AssignRoles, Resources.Web_Perm_AssignRoles),
            // AssignDepartments is intentionally NOT offered here: department
            // membership is edited through DepartmentViewModel and enforced via
            // EditDepartment. A standalone bit would be dead (never checked).
        ]),
        new(Resources.Web_PermGroup_ShareMgmt,
        [
            new(ManagementPermission.CreateShares, Resources.Web_Perm_CreateShares),
            new(ManagementPermission.DeleteShares, Resources.Web_Perm_DeleteShares),
            new(ManagementPermission.EditShareSettings, Resources.Web_Perm_EditSettings),
            new(ManagementPermission.ManageShareAccess, Resources.Web_Perm_ManageAccess),
            new(ManagementPermission.ManageShareAcls, Resources.Web_Perm_ManageAcls),
            new(ManagementPermission.ManageShareLinks,
                Resources.ResourceManager.GetString("Web_Perm_ManageShareLinks") ?? "Manage share links"),
            new(ManagementPermission.ManageUploadLinks,
                Resources.ResourceManager.GetString("Web_Perm_ManageUploadLinks") ?? "Manage upload links"),
        ]),
        new(Resources.Web_PermGroup_DeptMgmt,
        [
            new(ManagementPermission.EditDepartment, Resources.Web_Perm_EditDepartment),
            new(ManagementPermission.ViewDepartment, Resources.Web_Perm_ViewDepartment),
        ]),
        new(Resources.Web_PermGroup_SystemMgmt,
        [
            new(ManagementPermission.ManageSystemSettings, Resources.Web_Perm_ManageSystemSettings),
            new(ManagementPermission.ManageDataServices, Resources.Web_Perm_ManageDataServices),
            new(ManagementPermission.ManageCertificates, Resources.Web_Perm_ManageCertificates),
            new(ManagementPermission.ViewSystemLogs, Resources.Web_Perm_ViewSystemLogs),
            new(ManagementPermission.ManageBackups, Resources.Web_Perm_ManageBackups),
            new(ManagementPermission.ViewSecurityMonitor,
                Resources.ResourceManager.GetString("Web_Perm_ViewSecurityMonitor") ?? "View security overview"),
            new(ManagementPermission.ManageMailServer,
                Resources.ResourceManager.GetString("Web_Perm_ManageMailServer") ?? "Configure mail server"),
            new(ManagementPermission.ManageNotifications,
                Resources.ResourceManager.GetString("Web_Perm_ManageNotifications") ?? "Manage mail notifications"),
        ]),
        new(Resources.Web_PermGroup_ExternalStorage,
        [
            new(ManagementPermission.ManageCloudAccess, Resources.Web_Perm_ManageCloudAccess),
            new(ManagementPermission.ManageConnections, Resources.Web_Perm_ManageConnections),
            new(ManagementPermission.UseConnections, Resources.Web_Perm_UseConnections),
        ]),
        new(Resources.ResourceManager.GetString("Web_PermGroup_FileBackup") ?? "File backup",
        [
            new(ManagementPermission.ManageBackupRepositories,
                Resources.ResourceManager.GetString("Web_Perm_ManageBackupRepositories") ?? "Manage backup repositories"),
            new(ManagementPermission.ManageBackupJobs,
                Resources.ResourceManager.GetString("Web_Perm_ManageBackupJobs") ?? "Manage backup jobs"),
            new(ManagementPermission.RestoreFromBackup,
                Resources.ResourceManager.GetString("Web_Perm_RestoreFromBackup") ?? "Restore from backup"),
            new(ManagementPermission.RestoreBackupInPlace,
                Resources.ResourceManager.GetString("Web_Perm_RestoreBackupInPlace") ?? "Restore in place"),
        ]),
    ];

    public static readonly List<PermissionPreset> PermissionPresets =
    [
        new("UserAdmin", ManagementPermission.UserAdmin),
        new("GroupAdmin", ManagementPermission.GroupAdmin),
        new("ShareAdmin", ManagementPermission.ShareAdmin),
        new("DepartmentAdmin", ManagementPermission.DepartmentAdmin),
        new("SystemAdmin", ManagementPermission.SystemAdmin),
        new("ExternalStorageAdmin", ManagementPermission.CloudAccessAdmin),
        new("BackupAdmin", ManagementPermission.FileBackupAdmin),
        new("FullAdmin", ManagementPermission.FullAdmin),
    ];

    public void ApplyPermissionPreset(ManagementPermission preset)
        => EditRolePermissions = preset;

    // ══════════════════════════════════════════
    //  Edit Role
    // ══════════════════════════════════════════

    public async Task StartEditRoleAsync()
    {
        if (SelectedRole is null || _actorContext is null) return;

        // Role definitions are global; editing one requires Global AssignRoles.
        if (!await _mgmtAuth.HasGlobalPermissionAsync(_actorContext, ManagementPermission.AssignRoles))
        {
            ErrorMessage = Resources.Web_Error_NoPermission;
            return;
        }

        IsEditing = true;
        ErrorMessage = null; SuccessMessage = null;

        EditRoleName = SelectedRole.Name;
        EditRolePermissions = SelectedRole.ManagementPermissions;
    }

    public async Task SaveRoleAsync()
    {
        if (SelectedRole is null || _actorContext is null) return;

        // Role definitions are GLOBAL objects: editing a role's bitmask changes it in every
        // scope the role is assigned. Mutation therefore requires Global AssignRoles, and the
        // resulting permission set may never exceed the actor's own global effective
        // permissions — otherwise a role could be crafted to elevate beyond the actor.
        var globalPerms = await _mgmtAuth.GetEffectivePermissionsAtAsync(
            _actorContext, ScopeType.Global, Guid.Empty);

        if ((globalPerms & ManagementPermission.AssignRoles) == 0)
        {
            ErrorMessage = Resources.Web_Error_NoPermission;
            return;
        }

        if (!SelectedRole.IsSystemRole && (EditRolePermissions & ~globalPerms) != 0)
        {
            ErrorMessage = Resources.Web_Error_NoPermission;
            return;
        }

        if (!SelectedRole.IsSystemRole && string.IsNullOrWhiteSpace(EditRoleName))
        {
            ErrorMessage = Resources.Web_Role_NameRequired;
            return;
        }

        try
        {
            IsSaving = true;
            ErrorMessage = null;

            if (!SelectedRole.IsSystemRole)
            {
                // Role.Name is init-only, so a rename is applied by persisting a fresh
                // aggregate carrying the same identity, system flag and (edited) permissions.
                var renamed = new Role(
                    SelectedRole.Id, EditRoleName.Trim(),
                    EditRolePermissions, SelectedRole.IsSystemRole);
                await _roleRepo.UpdateAsync(renamed);
            }

            var refreshed = await _roleRepo.GetByIdAsync(SelectedRole.Id);
            if (refreshed is not null) SelectedRole = refreshed;
            await LoadRoleScopedAssignmentsAsync(SelectedRole.Id);

            IsEditing = false;
            SuccessMessage = Resources.Web_Role_Saved;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error saving role {RoleId}", SelectedRole.Id);
            ErrorMessage = Resources.Web_Error_SaveFailed;
        }
        finally { IsSaving = false; }
    }

    // ══════════════════════════════════════════
    //  Create Role
    // ══════════════════════════════════════════

    public void StartCreateRole()
    {
        if (!CanManageRoles) { ErrorMessage = Resources.Web_Error_NoPermission; return; }
        CancelEdit();
        SelectedUser = null; SelectedGroup = null; SelectedRole = null;
        IsCreatingRole = true; IsCreatingUser = false; IsCreatingGroup = false;
        CreateRoleName = "";
        ErrorMessage = null; SuccessMessage = null;
    }

    public async Task CreateRoleAsync()
    {
        ErrorMessage = null;
        if (string.IsNullOrWhiteSpace(CreateRoleName)) { ErrorMessage = Resources.Web_Role_NameRequired; return; }
        if (_actorContext is null) return;

        // Creating a (global) role definition requires Global AssignRoles.
        if (!await _mgmtAuth.HasGlobalPermissionAsync(_actorContext, ManagementPermission.AssignRoles))
        { ErrorMessage = Resources.Web_Error_NoPermission; return; }

        try
        {
            IsSaving = true;
            var role = new Role(Guid.NewGuid(), CreateRoleName.Trim());
            await _roleRepo.CreateAsync(role);
            IsCreatingRole = false; CreateRoleName = "";
            await LoadTabDataAsync();
            SuccessMessage = Resources.Web_Role_Created;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error creating the role");
            ErrorMessage = Resources.Web_Error_CreateFailed;
        }
        finally { IsSaving = false; }
    }
}

using Kaimo_File_Server.Core.Domain.Department;
using Kaimo_File_Server.Core.Domain.Identity;
using Kaimo_File_Server.Core.Helpers;
using Kaimo_File_Server.Core.Security;
using Kaimo_File_Server.Core.Services.Notifications;
using Kaimo_File_Server.Core.Language;
using Kaimo_File_Server.Web.Controllers.WebDav;
using Kaimo_File_Server.Web.Services;

namespace Kaimo_File_Server.Web.Components.ViewModels;

public partial class UserListViewModel
{
    /// <summary>
    /// Checks the "current password" of a self-service password change through the login
    /// service, so it is throttled and locked out exactly like a sign-in attempt (otherwise
    /// this form would be an unthrottled password oracle). Returns an error text or <c>null</c>.
    /// </summary>
    private async Task<string?> VerifyCurrentPasswordAsync(User user)
    {
        if (_loginService is null)
            return _passwordService.VerifyPassword(CurrentPassword, user.PasswordHash)
                ? null
                : Resources.Web_User_CurrentPasswordWrong;

        var result = await _loginService.AuthenticateAsync(user.Username, CurrentPassword, _client?.RemoteAddress, _client?.Host);
        return result.Outcome switch
        {
            LoginOutcome.Success when result.UserContext?.User.Id == user.Id => null,
            LoginOutcome.LockedOut => string.Format(
                Resources.ResourceManager.GetString("Web_User_CurrentPasswordLockedOut") ?? "{0}",
                Math.Max(1, (int)Math.Ceiling(result.RetryAfter.TotalMinutes))),
            _ => Resources.Web_User_CurrentPasswordWrong,
        };
    }

    /// <summary>
    /// After the user changed their own password the security stamp is new, which ends every
    /// session including this one. Re-issues the session token so only the other sessions end.
    /// </summary>
    private async Task RenewOwnSessionAsync(Guid userId)
    {
        if (_jwt is null || _authState is not JwtAuthenticationStateProvider provider)
            return;

        var context = await _userContextFactory.CreateByUserIdAsync(userId);
        if (context is null)
            return;

        var token = _jwt.GenerateToken(
            context.User.Id, context.User.Username, context.User.Name,
            context.Roles.Select(r => r.Name), securityStamp: context.User.SecurityStamp);
        if (!await provider.StoreSessionTokenAsync(token))
            _logger.LogWarning("Could not renew the session cookie after the password change; " +
                               "this session ends at the next revalidation");
    }

    private void InvalidateCachedWebDavLogins(Guid userId)
    {
        if (_cache is not null)
            WebDavBasicAuthenticationHandler.Invalidate(_cache, userId);
    }

    /// <summary>Loads the globally configured password requirements.</summary>
    private Task<PasswordPolicy> GetPasswordPolicyAsync()
        => _config.GetAsync(PasswordPolicy.ConfigKey, PasswordPolicy.Default());

    // -- User-Edit --
    public string EditUserName { get; set; } = "";
    public string EditUserDescription { get; set; } = "";
    public string EditUserEmail { get; set; } = "";
    public string EditUserFirstName { get; set; } = "";
    public string EditUserLastName { get; set; } = "";
    public bool EditUserIsEnabled { get; set; } = true;
    public bool EditUserCanChangePassword { get; set; } = true;
    public bool EditUserHomeDirectoryEnabled { get; set; } = true;

    /// <summary>
    /// Whether the per-user home toggle is offered: the actor holds global
    /// <see cref="ManagementPermission.ManageHomes"/> and home folders are enabled globally.
    /// </summary>
    public bool CanManageHomes { get; private set; }
    public List<CheckboxItem<Group>> EditUserGroups { get; private set; } = [];
    public List<CheckboxItem<Department>> EditUserDepartments { get; private set; } = [];


    /// <summary>Current password, required when a user changes their OWN password (self-service).</summary>
    public string CurrentPassword { get; set; } = "";

    // -- User-Details --
    public List<Group> UserGroups { get; private set; } = [];
    public List<Role> UserRoles { get; private set; } = [];
    public List<Department> UserDepartments { get; private set; } = [];
    public List<ScopedAssignmentDisplayItem> UserScopedAssignments { get; private set; } = [];

    // -- Password-Change --
    public string NewPassword { get; set; } = "";
    public string ConfirmPassword { get; set; } = "";

    // -- Create User --
    public bool IsCreatingUser { get; set; }
    public string CreateUserName { get; set; } = "";
    public string CreateUserUsername { get; set; } = "";
    public string CreateUserPassword { get; set; } = "";
    public string CreateUserDescription { get; set; } = "";
    public string CreateUserEmail { get; set; } = "";
    public bool CreateUserIsEnabled { get; set; } = true;
    public bool CreateUserCanChangePassword { get; set; } = true;
    public Guid? CreateUserDepartmentId { get; set; }

    // ══════════════════════════════════════════
    //  Edit User
    // ══════════════════════════════════════════

    public async Task StartEditUserAsync()
    {
        if (SelectedUser is null || _actorContext is null) return;

        // Full profile authority OR editing one's own record (Tier-1 personal fields).
        if (!CanEditSelectedUser && !IsViewingSelf)
        {
            ErrorMessage = Resources.Web_User_NoPermissionEdit;
            return;
        }

        IsEditing = true;
        ErrorMessage = null; SuccessMessage = null;
        NewPassword = ""; ConfirmPassword = ""; CurrentPassword = "";

        // Personal fields — editable by the user themselves and by full-profile admins.
        EditUserFirstName = SelectedUser.FirstName ?? "";
        EditUserLastName = SelectedUser.LastName ?? "";
        EditUserPhoto = SelectedUser.Photo;
        EditUserPhotoContentType = SelectedUser.PhotoContentType;
        _photoChanged = false;

        // Security/identity/membership fields are only populated (and shown) for a full-profile
        // admin; a self-service user never touches them.
        if (!CanEditSelectedUser) return;

        EditUserName = SelectedUser.Name;
        EditUserDescription = SelectedUser.Description ?? "";
        EditUserEmail = SelectedUser.Email ?? "";
        EditUserIsEnabled = SelectedUser.IsEnabled;
        EditUserCanChangePassword = SelectedUser.CanChangePassword;
        EditUserHomeDirectoryEnabled = SelectedUser.HomeDirectoryEnabled;

        var departmentIds = UserDepartments.Select(department => department.Id).ToHashSet();
        var allGroups = (await _groupRepo.GetAllAsync())
            .Where(group => group.DepartmentId == WellKnownGUIDs.DEPARTMENT_GLOBAL
                         || departmentIds.Contains(group.DepartmentId))
            .OrderBy(group => group.Name)
            .ToList();
        var userGroups = await _userRepo.GetGroupsForUserAsync(SelectedUser.Id);
        var userGroupIds = userGroups.Select(g => g.Id).ToHashSet();
        EditUserGroups = allGroups.Select(g => new CheckboxItem<Group>(g, userGroupIds.Contains(g.Id))).ToList();

        // Departments the actor may manage; pre-checked where the user is already a member.
        // Memberships in departments outside this authorized set stay untouched on save.
        EditUserDepartments = AuthorizedDepartmentsForAssign
            .Select(d => new CheckboxItem<Department>(d, departmentIds.Contains(d.Id))).ToList();
    }

    public async Task SaveUserAsync()
    {
        if (SelectedUser is null || _actorContext is null) return;

        if (!await _mgmtAuth.CanManageUserAsync(
                _actorContext, SelectedUser.Id, ManagementPermission.EditUserProfiles))
        {
            // No full authority → only the self-service subset (own personal fields + own
            // password) is permitted, and only on one's own record.
            if (!IsViewingSelf) { ErrorMessage = Resources.Web_Error_NoPermission; return; }
            await SaveSelfProfileAsync();
            return;
        }

        // Roles are assigned through the scoped role assignments, not in this form. The only
        // admin switch here is the Admins group, which mirrors the system-wide admin role.
        var wasInAdminsGroup = UserGroups.Any(g => g.Id == WellKnownGUIDs.GROUP_ADMINS);
        var leavesAdminsGroup = wasInAdminsGroup
            && EditUserGroups.Any(g => g.Item.Id == WellKnownGUIDs.GROUP_ADMINS && !g.IsChecked);

        // Last-admin protection: if this user is currently the SOLE enabled global admin,
        // the save must not strip that — neither by disabling the account nor by leaving
        // the Admins group (admin inherited through another group survives that).
        var adminUserIds = await GetEnabledGlobalAdminUserIdsAsync();
        if (adminUserIds.Contains(SelectedUser.Id) && !adminUserIds.Any(id => id != SelectedUser.Id))
        {
            var willBeAdmin = EditUserIsEnabled
                && (!leavesAdminsGroup
                    || await UserInheritsGlobalAdminFromGroupAsync(SelectedUser.Id, WellKnownGUIDs.GROUP_ADMINS));
            if (!willBeAdmin) { ErrorMessage = Resources.Web_Error_LastAdmin; return; }
        }

        try
        {
            IsSaving = true;
            ErrorMessage = null;

            if (EditUserName != SelectedUser.Name && !string.IsNullOrWhiteSpace(EditUserName))
                await _userRepo.UpdateNameAsync(SelectedUser.Id, EditUserName.Trim());

            await _userRepo.UpdateProfileAsync(
                SelectedUser.Id, EditUserDescription.Trim(), EditUserEmail.Trim(),
                EditUserIsEnabled, EditUserCanChangePassword);

            await _userRepo.UpdatePersonalNamesAsync(
                SelectedUser.Id, NullIfBlank(EditUserFirstName), NullIfBlank(EditUserLastName));
            if (CanManageHomes && EditUserHomeDirectoryEnabled != SelectedUser.HomeDirectoryEnabled)
                await _homes!.SetEnabledAsync(SelectedUser.Id, EditUserHomeDirectoryEnabled);
            if (_photoChanged)
                await _userRepo.UpdatePhotoAsync(SelectedUser.Id, EditUserPhoto, EditUserPhotoContentType);

            if (!string.IsNullOrWhiteSpace(NewPassword))
            {
                if (!await _mgmtAuth.CanManageUserAsync(
                        _actorContext, SelectedUser.Id, ManagementPermission.ResetPasswords))
                { ErrorMessage = Resources.Web_User_NoPermissionPasswordReset; return; }
                var pwError = (await GetPasswordPolicyAsync()).Validate(NewPassword);
                if (pwError is not null) { ErrorMessage = pwError; return; }
                if (NewPassword != ConfirmPassword) { ErrorMessage = Resources.Web_User_PasswordsDoNotMatch; return; }
                await _userRepo.UpdatePasswordAsync(SelectedUser.Id,
                    _passwordService.HashPassword(NewPassword),
                    _ntHashProtector.Protect(_passwordService.ComputeNtHash(NewPassword)));
                await PublishAsync(NotificationEvents.PasswordReset(
                    SelectedUser.Id, SelectedUser.Name, SelectedUser.Username, _actorContext?.User.Id));
                // An admin resetting their own password keeps this session (others end).
                if (SelectedUser.Id == _actorContext?.User.Id)
                    await RenewOwnSessionAsync(SelectedUser.Id);
            }

            // The edit may have disabled the account or reset its password: drop cached
            // WebDAV logins so neither the old password nor a disabled account stays usable.
            InvalidateCachedWebDavLogins(SelectedUser.Id);

            // Department membership — diff against the picker's authorized subset only, so
            // memberships in departments the actor cannot manage are never touched. Each change
            // is still gated per-department by EditDepartment.
            var finalDeptIds = UserDepartments.Select(d => d.Id).ToHashSet();
            if (CanAssignDepartments)
            {
                var shownDeptIds = EditUserDepartments.Select(d => d.Item.Id).ToHashSet();
                var desiredDeptIds = EditUserDepartments.Where(d => d.IsChecked).Select(d => d.Item.Id).ToHashSet();

                foreach (var deptId in desiredDeptIds.Except(finalDeptIds))
                    if (await _mgmtAuth.CanManageDepartmentAsync(_actorContext, deptId, ManagementPermission.EditDepartment))
                        await _departmentRepo.AddUserAsync(deptId, SelectedUser.Id);

                foreach (var deptId in finalDeptIds.Intersect(shownDeptIds).Except(desiredDeptIds))
                    if (await _mgmtAuth.CanManageDepartmentAsync(_actorContext, deptId, ManagementPermission.EditDepartment))
                    {
                        await _departmentRepo.RemoveUserAsync(deptId, SelectedUser.Id);
                        // Mirror DepartmentViewModel: leaving a department drops group memberships tied to it.
                        var groups = await _userRepo.GetGroupsForUserAsync(SelectedUser.Id);
                        var remaining = groups.Where(g => g.DepartmentId != deptId).Select(g => g.Id).ToList();
                        if (remaining.Count != groups.Count)
                            await _userRepo.SetGroupsForUserAsync(SelectedUser.Id, remaining);
                    }

                finalDeptIds = (await _departmentRepo.GetDepartmentsForUserAsync(SelectedUser.Id)).Select(d => d.Id).ToHashSet();
            }

            // A user's groups must stay within the user's departments (or Global), so drop any
            // whose department the user no longer belongs to after the diff above.
            var selectedGroupIds = EditUserGroups.Where(g => g.IsChecked)
                .Where(g => g.Item.DepartmentId == WellKnownGUIDs.DEPARTMENT_GLOBAL
                            || finalDeptIds.Contains(g.Item.DepartmentId))
                .Select(g => g.Item.Id).ToList();
            if (await _mgmtAuth.CanManageUserAsync(_actorContext, SelectedUser.Id, ManagementPermission.AssignGroups))
                await _userRepo.SetGroupsForUserAsync(SelectedUser.Id, selectedGroupIds);

            // Admin role <-> Admins group are mirrored. Joining or leaving the group (as it was
            // actually persisted under its permission gate above) grants or revokes the role.
            var inAdminsGroup = (await _userRepo.GetGroupsForUserAsync(SelectedUser.Id))
                .Any(g => g.Id == WellKnownGUIDs.GROUP_ADMINS);
            if (inAdminsGroup != wasInAdminsGroup)
                await ReconcileAdminCouplingAsync(SelectedUser.Id, inAdminsGroup);

            await LoadTabDataAsync();
            SelectedUser = Users.FirstOrDefault(u => u.Id == SelectedUser.Id);
            if (SelectedUser is not null)
            {
                await ResolveSelectedUserPermissionsAsync(SelectedUser);
                UserRoles = (await _userRepo.GetRolesForUserAsync(SelectedUser.Id)).OrderBy(r => r.Name).ToList();
                UserGroups = (await _userRepo.GetGroupsForUserAsync(SelectedUser.Id)).OrderBy(g => g.Name).ToList();
                UserDepartments = (await _departmentRepo.GetDepartmentsForUserAsync(SelectedUser.Id)).OrderBy(d => d.Name).ToList();
                await LoadUserScopedAssignmentsAsync(SelectedUser.Id);
            }

            IsEditing = false;
            NewPassword = ""; ConfirmPassword = "";
            SuccessMessage = Resources.Web_ChangesSaved;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error saving user {UserId}", SelectedUser?.Id);
            ErrorMessage = Resources.Web_Error_SaveFailed;
        }
        finally { IsSaving = false; }
    }

    // ══════════════════════════════════════════
    //  Create User
    // ══════════════════════════════════════════

    public void StartCreateUser()
    {
        if (!CanCreateUsers) { ErrorMessage = Resources.Web_Error_NoPermission; return; }
        CancelEdit();
        SelectedUser = null; SelectedGroup = null; SelectedRole = null;
        IsCreatingUser = true; IsCreatingGroup = false; IsCreatingRole = false;
        CreateUserName = ""; CreateUserUsername = ""; CreateUserPassword = "";
        CreateUserDescription = ""; CreateUserEmail = "";
        CreateUserIsEnabled = true; CreateUserCanChangePassword = true;
        CreateUserDepartmentId = AuthorizedDepartmentsForCreate.FirstOrDefault()?.Id;
        ErrorMessage = null; SuccessMessage = null;
    }

    public async Task CreateUserAsync()
    {
        if (_actorContext == null) return;
        ErrorMessage = null;

        if (string.IsNullOrWhiteSpace(CreateUserUsername)) { ErrorMessage = Resources.Web_User_NameRequired; return; }
        if (!SambaName.IsValidUsername(CreateUserUsername.Trim()))
        { ErrorMessage = string.Format(Resources.Web_User_UsernameInvalid, SambaName.MaxUsernameBytes); return; }
        if (string.IsNullOrWhiteSpace(CreateUserName)) { ErrorMessage = Resources.Web_Error_NameRequired; return; }
        if (string.IsNullOrWhiteSpace(CreateUserPassword))
        { ErrorMessage = Resources.Web_User_PasswordMinLength; return; }
        var createPwError = (await GetPasswordPolicyAsync()).Validate(CreateUserPassword);
        if (createPwError is not null) { ErrorMessage = createPwError; return; }
        if (CreateUserDepartmentId is null) { ErrorMessage = Resources.Web_Dept_SelectDepartment; return; }

        if (!await _mgmtAuth.CanCreateUserInDepartmentAsync(_actorContext, CreateUserDepartmentId.Value))
        { ErrorMessage = Resources.Web_Dept_NoPermissionInDept; return; }

        try
        {
            IsSaving = true;

            var existing = await _userRepo.GetByUsernameAsync(CreateUserUsername.Trim());
            if (existing is not null) { ErrorMessage = Resources.Web_User_NameTaken; return; }

            var user = new User(
                Guid.NewGuid(), CreateUserName.Trim(), CreateUserUsername.Trim(),
                _passwordService.HashPassword(CreateUserPassword),
                _ntHashProtector.Protect(_passwordService.ComputeNtHash(CreateUserPassword)),
                description: CreateUserDescription.Trim(), email: CreateUserEmail.Trim(),
                isEnabled: CreateUserIsEnabled, canChangePassword: CreateUserCanChangePassword);

            await _userRepo.CreateAsync(user);
            // Membership in the Global department is implicit for every user, so it is never stored.
            if (CreateUserDepartmentId.Value != WellKnownGUIDs.DEPARTMENT_GLOBAL)
                await _departmentRepo.AddUserAsync(CreateUserDepartmentId.Value, user.Id);
            // Every user is a member of the global Everyone group.
            await _groupRepo.AddMemberAsync(WellKnownGUIDs.GROUP_EVERYONE, user.Id);
            await EnsureHomeQuietlyAsync(user);
            await PublishAsync(NotificationEvents.UserCreated(
                user.Id, user.Name, user.Username, _actorContext.User.Id));

            IsCreatingUser = false;
            await LoadTabDataAsync();
            SuccessMessage = Resources.Web_User_Created;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error creating the user");
            ErrorMessage = Resources.Web_Error_CreateFailed;
        }
        finally { IsSaving = false; }
    }

    // Best-effort by contract: the publisher never throws.
    private Task PublishAsync(Kaimo_File_Server.Core.Domain.Notifications.NotificationEvent notification)
        => _notifications?.PublishAsync(notification) ?? Task.CompletedTask;

    // A missing home is recreated by the startup backfill or on first use, so a failure here
    // (e.g. pool not mounted) must not fail the user creation itself.
    private async Task EnsureHomeQuietlyAsync(User user)
    {
        if (_homes is null) return;
        try { await _homes.EnsureHomeAsync(user); }
        catch (Exception ex) { _logger.LogWarning(ex, "Could not provision the home folder of {UserId}", user.Id); }
    }
}

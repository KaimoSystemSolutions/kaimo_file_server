using Kaimo_File_Server.Core.Domain;
using Kaimo_File_Server.Core.Domain.Department;
using Kaimo_File_Server.Core.Domain.Identity;
using Kaimo_File_Server.Core.Helpers;
using Kaimo_File_Server.Core.Repositories;
using Kaimo_File_Server.Core.Security;
using Kaimo_File_Server.Core.Services;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.Extensions.Logging;
using Kaimo_File_Server.Core.Language;
using Kaimo_File_Server.Infrastructure.Configuration;

namespace Kaimo_File_Server.Web.Components.ViewModels;

public enum AdminTab { Users, Groups, Roles }

public class UserListViewModel
{
    private readonly IUserRepository _userRepo;
    private readonly IGroupRepository _groupRepo;
    private readonly IRoleRepository _roleRepo;
    private readonly IDepartmentRepository _departmentRepo;
    private readonly IScopedRoleAssignmentRepository _assignmentRepo;
    private readonly IPasswordService _passwordService;
    private readonly INtHashProtector _ntHashProtector;
    private readonly IManagementAuthService _mgmtAuth;
    private readonly IUserContextFactory _userContextFactory;
    private readonly AuthenticationStateProvider _authState;
    private readonly ILogger<UserListViewModel> _logger;
    private readonly IShareRepository _shareRepo;
    private readonly IConfigRepository _config;

    public UserListViewModel(
        IUserRepository userRepo,
        IGroupRepository groupRepo,
        IRoleRepository roleRepo,
        IDepartmentRepository departmentRepo,
        IScopedRoleAssignmentRepository assignmentRepo,
        IPasswordService passwordService,
        INtHashProtector ntHashProtector,
        IManagementAuthService mgmtAuth,
        IUserContextFactory userContextFactory,
        AuthenticationStateProvider authState,
        ILogger<UserListViewModel> logger,
        IShareRepository shareRepo,
        IConfigRepository config)
    {
        _userRepo = userRepo;
        _groupRepo = groupRepo;
        _roleRepo = roleRepo;
        _departmentRepo = departmentRepo;
        _assignmentRepo = assignmentRepo;
        _passwordService = passwordService;
        _ntHashProtector = ntHashProtector;
        _mgmtAuth = mgmtAuth;
        _userContextFactory = userContextFactory;
        _authState = authState;
        _logger = logger;
        _shareRepo = shareRepo;
        _config = config;
    }

    /// <summary>Loads the globally configured password requirements.</summary>
    private Task<PasswordPolicy> GetPasswordPolicyAsync()
        => _config.GetAsync(PasswordPolicy.ConfigKey, PasswordPolicy.Default());

    // ══════════════════════════════════════════
    //  Actor Context
    // ══════════════════════════════════════════

    private UserContext? _actorContext;

    // ══════════════════════════════════════════
    //  State
    // ══════════════════════════════════════════

    public List<User> Users { get; private set; } = [];
    public List<Group> Groups { get; private set; } = [];
    public List<Role> Roles { get; private set; } = [];
    public bool IsLoading { get; private set; }
    public string? ErrorMessage { get; private set; }
    public string? SuccessMessage { get; private set; }
    public AdminTab ActiveTab { get; private set; } = AdminTab.Users;

    // -- Actor Permissions --
    public bool IsGlobalAdmin { get; private set; }
    public bool CanAccessPage { get; private set; }
    public bool CanCreateUsers { get; private set; }
    public bool CanManageGroups { get; private set; }

    /// <summary>Can the actor <em>assign</em> existing roles (within some scope)? Governs the
    /// Roles tab and the scoped-assignment add/remove UI. The actual per-assignment limit is
    /// enforced server-side by <see cref="IManagementAuthService.CanAssignRoleAsync"/>.</summary>
    public bool CanManageRoles { get; private set; }

    /// <summary>Can the actor create/edit/delete role <em>definitions</em>? Roles are GLOBAL
    /// objects, so mutating them requires Global-scoped AssignRoles — a scoped delegate may
    /// assign roles but never redefine them.</summary>
    public bool CanEditRoleDefinitions { get; private set; }

    public bool CanDeleteUsers { get; private set; }
    public bool CanDeleteGroups { get; private set; }
    public bool CanDeleteRoles { get; private set; }

    public List<Department> AuthorizedDepartmentsForCreate { get; private set; } = [];
    public List<Department> AuthorizedDepartmentsForView { get; private set; } = [];

    /// <summary>All departments for display purposes (group/user detail, selectors).</summary>
    public List<Department> AllDepartments { get; private set; } = [];

    // -- Per-Selection Permissions --
    public bool CanEditSelectedUser { get; private set; }
    public bool CanDeleteSelectedUser { get; private set; }
    public bool CanResetPasswordForSelected { get; private set; }

    // -- Selection --
    public User? SelectedUser { get; set; }
    public Group? SelectedGroup { get; set; }
    public Role? SelectedRole { get; set; }

    // -- Edit-Modus --
    public bool IsEditing { get; private set; }
    public bool IsSaving { get; private set; }

    // -- User-Edit --
    public string EditUserName { get; set; } = "";
    public string EditUserDescription { get; set; } = "";
    public string EditUserEmail { get; set; } = "";
    public bool EditUserIsEnabled { get; set; } = true;
    public bool EditUserCanChangePassword { get; set; } = true;
    public List<CheckboxItem<Group>> EditUserGroups { get; private set; } = [];
    public List<CheckboxItem<Role>> EditUserRoles { get; private set; } = [];

    // -- User-Details --
    public List<Group> UserGroups { get; private set; } = [];
    public List<Role> UserRoles { get; private set; } = [];
    public List<Department> UserDepartments { get; private set; } = [];
    public List<ScopedAssignmentDisplayItem> UserScopedAssignments { get; private set; } = [];

    // -- Group-Details --
    public List<User> GroupMembers { get; private set; } = [];

    // -- Role-Details --
    public List<ScopedAssignmentDisplayItem> RoleScopedAssignments { get; private set; } = [];

    // -- Password-Change --
    public string NewPassword { get; set; } = "";
    public string ConfirmPassword { get; set; } = "";

    // -- Group-Edit --
    public List<CheckboxItem<User>> EditGroupMembers { get; private set; } = [];

    // -- Role-Edit --
    public ManagementPermission EditRolePermissions { get; set; } = ManagementPermission.None;

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

    // -- Create Group --
    public bool IsCreatingGroup { get; set; }
    public string CreateGroupName { get; set; } = "";
    public Guid CreateGroupDepartmentId { get; set; } = WellKnownGUIDs.DEPARTMENT_GLOBAL;

    // -- Create Role --
    public bool IsCreatingRole { get; set; }
    public string CreateRoleName { get; set; } = "";

    // -- Delete Confirmation --
    public bool IsConfirmingDelete { get; set; }

    // -- Scoped Assignment Creation --
    public bool IsAddingAssignment { get; set; }
    public ScopeType NewAssignmentScopeType { get; set; } = ScopeType.Global;
    public Guid? NewAssignmentScopeId { get; set; }
    public Guid? NewAssignmentPrincipalId { get; set; }
    public bool NewAssignmentPrincipalIsGroup { get; set; }
    public List<User> AllUsers { get; private set; } = [];
    public List<Group> AllGroups { get; private set; } = [];

    // ══════════════════════════════════════════
    //  Computed
    // ══════════════════════════════════════════

    public string TabSubtitle => ActiveTab switch
    {
        AdminTab.Users => string.Format(Resources.Web_Tab_UsersCount, Users.Count),
        AdminTab.Groups => string.Format(Resources.Web_Tab_GroupsCount, Groups.Count),
        AdminTab.Roles => string.Format(Resources.Web_Tab_RolesCount, Roles.Count),
        _ => ""
    };

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
        ]),
    ];

    public static readonly List<PermissionPreset> PermissionPresets =
    [
        new("UserAdmin", ManagementPermission.UserAdmin),
        new("GroupAdmin", ManagementPermission.GroupAdmin),
        new("ShareAdmin", ManagementPermission.ShareAdmin),
        new("DepartmentAdmin", ManagementPermission.DepartmentAdmin),
        new("SystemAdmin", ManagementPermission.SystemAdmin),
        new("FullAdmin", ManagementPermission.FullAdmin),
    ];

    public void ApplyPermissionPreset(ManagementPermission preset)
        => EditRolePermissions = preset;

    // ══════════════════════════════════════════
    //  Department name resolver (for display)
    // ══════════════════════════════════════════

    public string GetDepartmentName(Guid departmentId)
    {
        if (departmentId == WellKnownGUIDs.DEPARTMENT_GLOBAL) return "Global";
        var dept = AllDepartments.FirstOrDefault(d => d.Id == departmentId);
        return dept?.Name ?? departmentId.ToString();
    }

    // ══════════════════════════════════════════
    //  Load & Permission Resolution
    // ══════════════════════════════════════════

    public async Task LoadAsync()
    {
        try
        {
            IsLoading = true;
            ErrorMessage = null;

            _actorContext = await BuildActorContextAsync();
            if (_actorContext == null)
            {
                CanAccessPage = false;
                ErrorMessage = Resources.Web_Error_NotLoggedIn;
                return;
            }

            AllDepartments = (await _departmentRepo.GetAllAsync()).OrderBy(d => d.Name).ToList();

            await ResolveActorPermissionsAsync();

            if (!CanAccessPage)
            {
                ErrorMessage = Resources.Web_Error_NoManagementPermission;
                return;
            }

            await LoadTabDataAsync();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error loading the user management");
            ErrorMessage = Resources.Web_Error_LoadFailedGeneric;
        }
        finally { IsLoading = false; }
    }

    private async Task<UserContext?> BuildActorContextAsync()
    {
        var state = await _authState.GetAuthenticationStateAsync();
        var username = state.User.Identity?.Name;
        if (string.IsNullOrEmpty(username)) return null;
        return await _userContextFactory.CreateByUsernameAsync(username);
    }

    private async Task ResolveActorPermissionsAsync()
    {
        if (_actorContext == null) return;

        IsGlobalAdmin = await _mgmtAuth.HasGlobalPermissionAsync(
            _actorContext, ManagementPermission.FullAdmin);

        CanCreateUsers = await _mgmtAuth.HasAnyPermissionAsync(
            _actorContext, ManagementPermission.CreateUsers);

        var canEditUsers = await _mgmtAuth.HasAnyPermissionAsync(
            _actorContext, ManagementPermission.EditUserProfiles);

        CanManageGroups = await _mgmtAuth.HasAnyPermissionAsync(
            _actorContext, ManagementPermission.CreateGroups
                         | ManagementPermission.ManageGroupMembers);

        CanManageRoles = await _mgmtAuth.HasAnyPermissionAsync(
            _actorContext, ManagementPermission.AssignRoles);

        // Role definitions are global objects → editing them requires Global AssignRoles.
        CanEditRoleDefinitions = await _mgmtAuth.HasGlobalPermissionAsync(
            _actorContext, ManagementPermission.AssignRoles);

        CanDeleteUsers = await _mgmtAuth.HasAnyPermissionAsync(
            _actorContext, ManagementPermission.DeleteUsers);

        CanDeleteGroups = await _mgmtAuth.HasAnyPermissionAsync(
            _actorContext, ManagementPermission.DeleteGroups);

        CanDeleteRoles = CanEditRoleDefinitions;

        CanAccessPage = CanCreateUsers || canEditUsers || CanManageGroups || CanManageRoles;

        // Resolve authorized departments for user creation
        var createResult = await _mgmtAuth.GetAuthorizedDepartmentIdsAsync(
            _actorContext, ManagementPermission.CreateUsers);
        AuthorizedDepartmentsForCreate = await LoadDepartmentsFromScopeResultAsync(createResult);

        var editResult = await _mgmtAuth.GetAuthorizedDepartmentIdsAsync(
            _actorContext, ManagementPermission.EditUserProfiles);
        var viewResult = await _mgmtAuth.GetAuthorizedDepartmentIdsAsync(
            _actorContext, ManagementPermission.ViewDepartment);

        if (editResult.IsUnrestricted || viewResult.IsUnrestricted)
            AuthorizedDepartmentsForView = await _departmentRepo.GetAllAsync();
        else
        {
            var mergedIds = editResult.ScopeIds.Concat(viewResult.ScopeIds).Distinct().ToList();
            AuthorizedDepartmentsForView = await LoadDepartmentsByIdsAsync(mergedIds);
        }

        CreateUserDepartmentId = AuthorizedDepartmentsForCreate.FirstOrDefault()?.Id;
    }

    private async Task ResolveSelectedUserPermissionsAsync(User user)
    {
        if (_actorContext == null)
        {
            CanEditSelectedUser = false;
            CanDeleteSelectedUser = false;
            CanResetPasswordForSelected = false;
            return;
        }

        CanEditSelectedUser = await _mgmtAuth.CanManageUserAsync(
            _actorContext, user.Id, ManagementPermission.EditUserProfiles);
        CanDeleteSelectedUser = await _mgmtAuth.CanManageUserAsync(
            _actorContext, user.Id, ManagementPermission.DeleteUsers);
        CanResetPasswordForSelected = await _mgmtAuth.CanManageUserAsync(
            _actorContext, user.Id, ManagementPermission.ResetPasswords);
    }

    private async Task<List<Department>> LoadDepartmentsFromScopeResultAsync(AuthorizedScopeResult result)
    {
        if (result.IsUnrestricted) return await _departmentRepo.GetAllAsync();
        if (result.ScopeIds.Count == 0) return [];
        return await LoadDepartmentsByIdsAsync(result.ScopeIds.ToList());
    }

    private async Task<List<Department>> LoadDepartmentsByIdsAsync(List<Guid> ids)
    {
        if (ids.Count == 0) return [];
        var all = await _departmentRepo.GetAllAsync();
        return all.Where(d => ids.Contains(d.Id)).OrderBy(d => d.Name).ToList();
    }

    // ══════════════════════════════════════════
    //  Tab Switching
    // ══════════════════════════════════════════

    public async Task SwitchTabAsync(AdminTab tab)
    {
        ActiveTab = tab;
        CancelEdit(); CancelCreate();
        SelectedUser = null; SelectedGroup = null; SelectedRole = null;
        ErrorMessage = null; SuccessMessage = null;

        try
        {
            IsLoading = true;
            await LoadTabDataAsync();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error switching to tab {Tab}", tab);
            ErrorMessage = Resources.Web_Error_LoadFailedGeneric;
        }
        finally { IsLoading = false; }
    }

    // ══════════════════════════════════════════
    //  Selection
    // ══════════════════════════════════════════

    public async Task SelectUserAsync(User user)
    {
        CancelEdit(); CancelCreate();
        SelectedGroup = null; SelectedRole = null;
        SuccessMessage = null;

        if (SelectedUser?.Id == user.Id)
        {
            SelectedUser = null;
            UserRoles = []; UserGroups = []; UserDepartments = []; UserScopedAssignments = [];
            CanEditSelectedUser = false; CanDeleteSelectedUser = false; CanResetPasswordForSelected = false;
            return;
        }

        SelectedUser = user;
        await ResolveSelectedUserPermissionsAsync(user);
        UserRoles = (await _userRepo.GetRolesForUserAsync(user.Id)).OrderBy(r => r.Name).ToList();
        UserGroups = (await _userRepo.GetGroupsForUserAsync(user.Id)).OrderBy(g => g.Name).ToList();
        UserDepartments = (await _departmentRepo.GetDepartmentsForUserAsync(user.Id)).OrderBy(d => d.Name).ToList();
        await LoadUserScopedAssignmentsAsync(user.Id);
    }

    public async Task SelectGroupAsync(Group group)
    {
        CancelEdit(); CancelCreate();
        SelectedUser = null; SelectedRole = null;
        SuccessMessage = null;

        if (SelectedGroup?.Id == group.Id)
        {
            SelectedGroup = null;
            GroupMembers = [];
            return;
        }

        SelectedGroup = group;
        GroupMembers = (await _groupRepo.GetMembersAsync(group.Id)).OrderBy(u => u.Name).ToList();
    }

    public async Task SelectRoleAsync(Role role)
    {
        CancelEdit(); CancelCreate();
        SelectedUser = null; SelectedGroup = null;
        SuccessMessage = null;

        if (SelectedRole?.Id == role.Id)
        {
            SelectedRole = null;
            RoleScopedAssignments = [];
            return;
        }

        SelectedRole = role;
        await LoadRoleScopedAssignmentsAsync(role.Id);
    }

    // ══════════════════════════════════════════
    //  Edit User
    // ══════════════════════════════════════════

    public async Task StartEditUserAsync()
    {
        if (SelectedUser is null || _actorContext is null) return;

        if (!await _mgmtAuth.CanManageUserAsync(
                _actorContext, SelectedUser.Id, ManagementPermission.EditUserProfiles))
        {
            ErrorMessage = Resources.Web_User_NoPermissionEdit;
            return;
        }

        IsEditing = true;
        ErrorMessage = null; SuccessMessage = null;
        NewPassword = ""; ConfirmPassword = "";

        EditUserName = SelectedUser.Name;
        EditUserDescription = SelectedUser.Description ?? "";
        EditUserEmail = SelectedUser.Email ?? "";
        EditUserIsEnabled = SelectedUser.IsEnabled;
        EditUserCanChangePassword = SelectedUser.CanChangePassword;

        var departmentIds = UserDepartments.Select(department => department.Id).ToHashSet();
        var allGroups = (await _groupRepo.GetAllAsync())
            .Where(group => group.DepartmentId == WellKnownGUIDs.DEPARTMENT_GLOBAL
                         || departmentIds.Contains(group.DepartmentId))
            .OrderBy(group => group.Name)
            .ToList();
        var userGroups = await _userRepo.GetGroupsForUserAsync(SelectedUser.Id);
        var userGroupIds = userGroups.Select(g => g.Id).ToHashSet();
        EditUserGroups = allGroups.Select(g => new CheckboxItem<Group>(g, userGroupIds.Contains(g.Id))).ToList();

        var allRoles = (await _roleRepo.GetAllAsync()).OrderBy(r => r.Name).ToList();
        var userRoles = await _userRepo.GetRolesForUserAsync(SelectedUser.Id);
        var userRoleIds = userRoles.Select(r => r.Id).ToHashSet();
        EditUserRoles = allRoles.Select(r => new CheckboxItem<Role>(r, userRoleIds.Contains(r.Id))).ToList();
    }

    public async Task SaveUserAsync()
    {
        if (SelectedUser is null || _actorContext is null) return;

        if (!await _mgmtAuth.CanManageUserAsync(
                _actorContext, SelectedUser.Id, ManagementPermission.EditUserProfiles))
        {
            ErrorMessage = Resources.Web_Error_NoPermission;
            return;
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
            }

            var selectedGroupIds = EditUserGroups.Where(g => g.IsChecked).Select(g => g.Item.Id).ToList();
            if (await _mgmtAuth.CanManageUserAsync(_actorContext, SelectedUser.Id, ManagementPermission.AssignGroups))
                await _userRepo.SetGroupsForUserAsync(SelectedUser.Id, selectedGroupIds);

            var selectedRoleIds = EditUserRoles.Where(r => r.IsChecked).Select(r => r.Item.Id).ToList();
            if (await _mgmtAuth.CanManageUserAsync(_actorContext, SelectedUser.Id, ManagementPermission.AssignRoles))
                await _userRepo.SetRolesForUserAsync(SelectedUser.Id, selectedRoleIds);

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
            _logger.LogError(ex, "Error saving user {UserId}", SelectedUser.Id);
            ErrorMessage = Resources.Web_Error_SaveFailed;
        }
        finally { IsSaving = false; }
    }

    // ══════════════════════════════════════════
    //  Edit Group
    // ══════════════════════════════════════════

    public async Task StartEditGroupAsync()
    {
        if (SelectedGroup is null || _actorContext is null) return;

        if (!await _mgmtAuth.CanManageGroupAsync(
                _actorContext, SelectedGroup.Id, ManagementPermission.ManageGroupMembers))
        {
            ErrorMessage = Resources.Web_Error_NoPermission;
            return;
        }

        IsEditing = true;
        ErrorMessage = null; SuccessMessage = null;

        var allUsers = (await _userRepo.GetAllAsync()).OrderBy(u => u.Name).ToList();
        var members = await _groupRepo.GetMembersAsync(SelectedGroup.Id);
        var memberIds = members.Select(u => u.Id).ToHashSet();
        EditGroupMembers = allUsers.Select(u => new CheckboxItem<User>(u, memberIds.Contains(u.Id))).ToList();
    }

    public async Task SaveGroupAsync()
    {
        if (SelectedGroup is null || _actorContext is null) return;

        if (!await _mgmtAuth.CanManageGroupAsync(
                _actorContext, SelectedGroup.Id, ManagementPermission.ManageGroupMembers))
        {
            ErrorMessage = Resources.Web_Error_NoPermission;
            return;
        }

        try
        {
            IsSaving = true;
            ErrorMessage = null;

            var selectedUserIds = EditGroupMembers.Where(m => m.IsChecked).Select(m => m.Item.Id).ToList();
            await _groupRepo.SetMembersAsync(SelectedGroup.Id, selectedUserIds);
            GroupMembers = (await _groupRepo.GetMembersAsync(SelectedGroup.Id)).OrderBy(u => u.Name).ToList();

            IsEditing = false;
            SuccessMessage = Resources.Web_Members_Saved;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error saving group {GroupId}", SelectedGroup.Id);
            ErrorMessage = Resources.Web_Error_SaveFailed;
        }
        finally { IsSaving = false; }
    }

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

        try
        {
            IsSaving = true;
            ErrorMessage = null;

            if (!SelectedRole.IsSystemRole)
            {
                SelectedRole.ManagementPermissions = EditRolePermissions;
                await _roleRepo.UpdateAsync(SelectedRole);
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
    //  Scoped Role Assignments
    // ══════════════════════════════════════════

    public void StartAddAssignment()
    {
        IsAddingAssignment = true;
        NewAssignmentScopeType = ScopeType.Global;
        NewAssignmentScopeId = null;
        NewAssignmentPrincipalId = null;
        NewAssignmentPrincipalIsGroup = false;
        ErrorMessage = null;
    }

    public void CancelAddAssignment()
    {
        IsAddingAssignment = false;
        ErrorMessage = null;
    }

    public async Task LoadAssignmentFormDataAsync()
    {
        AllUsers = (await _userRepo.GetAllAsync()).OrderBy(u => u.Name).ToList();
        AllGroups = (await _groupRepo.GetAllAsync()).OrderBy(g => g.Name).ToList();
    }

    public async Task CreateAssignmentAsync()
    {
        if (SelectedRole is null || _actorContext is null) return;

        if (NewAssignmentPrincipalId is null)
        { ErrorMessage = Resources.Web_Error_SelectUserOrGroup; return; }
        if (NewAssignmentScopeType != ScopeType.Global && NewAssignmentScopeId is null)
        { ErrorMessage = Resources.Web_Scope_SelectScope; return; }

        var scopeId = NewAssignmentScopeType == ScopeType.Global
            ? Guid.Empty : NewAssignmentScopeId!.Value;

        // No-privilege-elevation gate: the actor must hold AssignRoles AT the chosen scope
        // and may not grant a role carrying any permission the actor lacks there. This blocks
        // the escalation where a narrowly-scoped delegate assigns e.g. Administrator globally.
        if (!await _mgmtAuth.CanAssignRoleAsync(
                _actorContext, SelectedRole.ManagementPermissions, NewAssignmentScopeType, scopeId))
        { ErrorMessage = Resources.Web_Error_NoPermission; return; }

        try
        {
            IsSaving = true;
            ErrorMessage = null;

            await _assignmentRepo.CreateAsync(new ScopedRoleAssignment(
                NewAssignmentPrincipalId.Value, SelectedRole.Id, NewAssignmentScopeType, scopeId));

            IsAddingAssignment = false;
            await LoadRoleScopedAssignmentsAsync(SelectedRole.Id);
            SuccessMessage = Resources.Web_Assignment_Created;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error creating the assignment");
            ErrorMessage = Resources.Web_Error_CreateAssignmentFailed;
        }
        finally { IsSaving = false; }
    }

    public async Task DeleteAssignmentAsync(Guid assignmentId)
    {
        if (_actorContext is null) return;

        // Removing an assignment is bounded by the same authority as creating one: the actor
        // must be able to assign that role at that scope. Otherwise any AssignRoles holder
        // could strip arbitrary assignments (e.g. remove the sole global admin).
        var assignment = await _assignmentRepo.GetByIdAsync(assignmentId);
        if (assignment is null) { ErrorMessage = Resources.Web_Error_NoPermission; return; }

        var role = await _roleRepo.GetByIdAsync(assignment.RoleId);
        var rolePerms = role?.ManagementPermissions ?? ManagementPermission.None;

        if (!await _mgmtAuth.CanAssignRoleAsync(
                _actorContext, rolePerms, assignment.ScopeType, assignment.ScopeId))
        { ErrorMessage = Resources.Web_Error_NoPermission; return; }

        try
        {
            IsSaving = true;
            ErrorMessage = null;
            await _assignmentRepo.DeleteAsync(assignmentId);

            if (SelectedRole is not null) await LoadRoleScopedAssignmentsAsync(SelectedRole.Id);
            if (SelectedUser is not null) await LoadUserScopedAssignmentsAsync(SelectedUser.Id);
            SuccessMessage = Resources.Web_Assignment_Removed;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error deleting assignment {Id}", assignmentId);
            ErrorMessage = Resources.Web_Error_DeleteAssignmentFailed;
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
        { ErrorMessage = $"Username must be 1-{SambaName.MaxUsernameBytes} ASCII bytes and contain only letters, digits, '.', '_' or '-'."; return; }
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
            await _departmentRepo.AddUserAsync(CreateUserDepartmentId.Value, user.Id);

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

    // ══════════════════════════════════════════
    //  Create Group (with Department selector)
    // ══════════════════════════════════════════

    public void StartCreateGroup()
    {
        if (!CanManageGroups) { ErrorMessage = Resources.Web_Error_NoPermission; return; }
        CancelEdit();
        SelectedUser = null; SelectedGroup = null; SelectedRole = null;
        IsCreatingGroup = true; IsCreatingUser = false; IsCreatingRole = false;
        CreateGroupName = "";
        CreateGroupDepartmentId = WellKnownGUIDs.DEPARTMENT_GLOBAL;
        ErrorMessage = null; SuccessMessage = null;
    }

    public async Task CreateGroupAsync()
    {
        ErrorMessage = null;
        if (string.IsNullOrWhiteSpace(CreateGroupName)) { ErrorMessage = Resources.Web_Group_NameRequired; return; }
        if (_actorContext is null) return;

        if (!await _mgmtAuth.HasAnyPermissionAsync(_actorContext, ManagementPermission.CreateGroups))
        { ErrorMessage = Resources.Web_Error_NoPermission; return; }

        try
        {
            IsSaving = true;
            var group = new Group(Guid.NewGuid(), CreateGroupName.Trim(), CreateGroupDepartmentId);
            await _groupRepo.CreateAsync(group);
            IsCreatingGroup = false; CreateGroupName = "";
            await LoadTabDataAsync();
            SuccessMessage = Resources.Web_Group_Created;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error creating the group");
            ErrorMessage = Resources.Web_Error_CreateFailed;
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

    // ══════════════════════════════════════════
    //  Delete
    // ══════════════════════════════════════════

    public void RequestDeleteUser() { IsConfirmingDelete = true; ErrorMessage = null; }
    public void RequestDeleteGroup() { IsConfirmingDelete = true; ErrorMessage = null; }
    public void RequestDeleteRole() { IsConfirmingDelete = true; ErrorMessage = null; }

    public async Task ConfirmDeleteUserAsync()
    {
        if (SelectedUser is null || _actorContext is null) return;
        if (!await _mgmtAuth.CanManageUserAsync(_actorContext, SelectedUser.Id, ManagementPermission.DeleteUsers))
        { ErrorMessage = Resources.Web_Error_NoPermission; return; }

        try
        {
            IsSaving = true;
            await _userRepo.DeleteAsync(SelectedUser.Id);
            SelectedUser = null; IsConfirmingDelete = false;
            await LoadTabDataAsync();
            SuccessMessage = Resources.Web_User_Deleted;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error deleting");
            ErrorMessage = Resources.Web_Error_DeleteFailed;
        }
        finally { IsSaving = false; }
    }

    public async Task ConfirmDeleteGroupAsync()
    {
        if (SelectedGroup is null || _actorContext is null) return;
        if (!await _mgmtAuth.CanManageGroupAsync(_actorContext, SelectedGroup.Id, ManagementPermission.DeleteGroups))
        { ErrorMessage = Resources.Web_Error_NoPermission; return; }

        try
        {
            IsSaving = true;
            await _groupRepo.DeleteAsync(SelectedGroup.Id);
            SelectedGroup = null; IsConfirmingDelete = false;
            await LoadTabDataAsync();
            SuccessMessage = Resources.Web_Group_Deleted;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error deleting");
            ErrorMessage = Resources.Web_Error_DeleteFailed;
        }
        finally { IsSaving = false; }
    }

    public async Task ConfirmDeleteRoleAsync()
    {
        if (SelectedRole is null || _actorContext is null) return;
        // Deleting a (global) role definition requires Global AssignRoles.
        if (!await _mgmtAuth.HasGlobalPermissionAsync(_actorContext, ManagementPermission.AssignRoles))
        { ErrorMessage = Resources.Web_Error_NoPermission; return; }

        try
        {
            IsSaving = true;
            await _roleRepo.DeleteAsync(SelectedRole.Id);
            SelectedRole = null; RoleScopedAssignments = [];
            IsConfirmingDelete = false;
            await LoadTabDataAsync();
            SuccessMessage = Resources.Web_Role_Deleted;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error deleting");
            ErrorMessage = Resources.Web_Error_DeleteFailed;
        }
        finally { IsSaving = false; }
    }

    // ══════════════════════════════════════════
    //  Helpers
    // ══════════════════════════════════════════

    public void CancelEdit()
    {
        IsEditing = false; IsSaving = false;
        IsConfirmingDelete = false; IsAddingAssignment = false;
        NewPassword = ""; ConfirmPassword = "";
        ErrorMessage = null;
    }

    public void CancelCreate()
    {
        IsCreatingUser = false; IsCreatingGroup = false; IsCreatingRole = false;
        IsConfirmingDelete = false;
        CreateUserName = ""; CreateUserUsername = ""; CreateUserPassword = "";
        CreateUserDescription = ""; CreateUserEmail = "";
        CreateUserIsEnabled = true; CreateUserCanChangePassword = true;
        CreateGroupName = ""; CreateRoleName = "";
        ErrorMessage = null;
    }

    private async Task LoadTabDataAsync()
    {
        switch (ActiveTab)
        {
            case AdminTab.Users: await LoadFilteredUsersAsync(); break;
            case AdminTab.Groups: await LoadFilteredGroupsAsync(); break;
            case AdminTab.Roles:
                Roles = (await _roleRepo.GetAllAsync()).OrderBy(r => r.Name).ToList();
                break;
        }
    }

    private async Task LoadFilteredUsersAsync()
    {
        var allUsers = (await _userRepo.GetAllAsync()).OrderBy(u => u.Name).ToList();

        if (IsGlobalAdmin) { Users = allUsers; return; }

        var authorizedDeptIds = AuthorizedDepartmentsForView.Select(d => d.Id).ToHashSet();
        if (authorizedDeptIds.Count == 0) { Users = []; return; }

        var filtered = new List<User>();
        foreach (var user in allUsers)
        {
            var userDepts = await _departmentRepo.GetDepartmentsForUserAsync(user.Id);
            if (userDepts.Any(d => authorizedDeptIds.Contains(d.Id)))
                filtered.Add(user);
        }
        Users = filtered;
    }

    private async Task LoadFilteredGroupsAsync()
    {
        var allGroups = (await _groupRepo.GetAllAsync()).OrderBy(g => g.Name).ToList();

        if (IsGlobalAdmin) { Groups = allGroups; return; }
        if (_actorContext is null) { Groups = []; return; }

        // Filter by the actor's GROUP-management scope — the departments in which the
        // actor may manage groups — NOT the department-view scope used for the user list.
        // A group is listed when its department is in that scope. This also fixes the old
        // "empty scope → show everything" leak: no group-management scope now means [].
        var scope = await _mgmtAuth.GetAuthorizedDepartmentIdsAnyAsync(
            _actorContext, ManagementPermission.GroupAdmin);

        if (scope.IsUnrestricted) { Groups = allGroups; return; }

        var authorizedDeptIds = scope.ScopeIds.ToHashSet();
        Groups = allGroups.Where(g => authorizedDeptIds.Contains(g.DepartmentId)).ToList();
    }

    private async Task LoadRoleScopedAssignmentsAsync(Guid roleId)
    {
        var allAssignments = await _assignmentRepo.GetByRoleAsync(roleId);
        var users = await _userRepo.GetAllAsync();
        var groups = await _groupRepo.GetAllAsync();
        RoleScopedAssignments = await ResolveScopedAssignmentsAsync(allAssignments, users, groups);
    }

    private async Task LoadUserScopedAssignmentsAsync(Guid userId)
    {
        var assignments = await _assignmentRepo.GetByPrincipalAsync(userId);
        var users = await _userRepo.GetAllAsync();
        var groups = await _groupRepo.GetAllAsync();
        UserScopedAssignments = await ResolveScopedAssignmentsAsync(assignments, users, groups);
    }

    private async Task<List<ScopedAssignmentDisplayItem>> ResolveScopedAssignmentsAsync(
        List<ScopedRoleAssignment> assignments,
        IEnumerable<User> users,
        IEnumerable<Group> groups)
    {
        var userLookup = users.ToDictionary(u => u.Id);
        var groupLookup = groups.ToDictionary(g => g.Id);
        var deptLookup = AllDepartments.ToDictionary(d => d.Id);
        var roles = await _roleRepo.GetAllAsync();
        var roleLookup = roles.ToDictionary(r => r.Id);

        var items = new List<ScopedAssignmentDisplayItem>();

        foreach (var a in assignments)
        {
            var principalName = userLookup.TryGetValue(a.PrincipalId, out var user)
                ? user.Name
                : groupLookup.TryGetValue(a.PrincipalId, out var group)
                    ? group.Name : a.PrincipalId.ToString();

            var isGroup = groupLookup.ContainsKey(a.PrincipalId);

            var roleName = roleLookup.TryGetValue(a.RoleId, out var role)
                ? role.Name : a.RoleId.ToString();

            var scopeName = a.ScopeType switch
            {
                ScopeType.Global => "Global",
                ScopeType.Department => deptLookup.TryGetValue(a.ScopeId, out var dept)
                    ? dept.Name : a.ScopeId.ToString(),
                ScopeType.Share => $"Share: {a.ScopeId}",
                _ => a.ScopeId.ToString()
            };

            items.Add(new ScopedAssignmentDisplayItem(
                a.Id, principalName, isGroup, roleName, a.ScopeType, scopeName));
        }

        return items.OrderBy(i => i.PrincipalName).ToList();
    }
}

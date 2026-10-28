using Kaimo_File_Server.Infrastructure.Services;
using Kaimo_File_Server.Core.Domain.Department;
using Kaimo_File_Server.Core.Domain.Identity;
using Kaimo_File_Server.Core.Helpers;
using Kaimo_File_Server.Core.Repositories;
using Kaimo_File_Server.Core.Security;
using Kaimo_File_Server.Core.Services;
using Kaimo_File_Server.Core.Services.Notifications;
using Microsoft.AspNetCore.Components.Authorization;
using Kaimo_File_Server.Core.Language;
using Kaimo_File_Server.Infrastructure.Configuration;
using Kaimo_File_Server.Web.Services;
using Microsoft.Extensions.Caching.Memory;

namespace Kaimo_File_Server.Web.Components.ViewModels;

public enum AdminTab { Users, Groups, Roles }

public partial class UserListViewModel
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
    private readonly HomeDirectoryService? _homes;
    private readonly ServiceAccessGate? _serviceAccess;
    private readonly ILoginService? _loginService;
    private readonly ClientConnectionInfo? _client;
    private readonly JwtTokenService? _jwt;
    private readonly IMemoryCache? _cache;
    private readonly INotificationPublisher? _notifications;
    private readonly RoleAssignmentService _roleAssignments;

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
        IConfigRepository config,
        ILoginService? loginService = null,
        ClientConnectionInfo? client = null,
        JwtTokenService? jwt = null,
        IMemoryCache? cache = null,
        HomeDirectoryService? homes = null,
        INotificationPublisher? notifications = null,
        ServiceAccessGate? serviceAccess = null)
    {
        _serviceAccess = serviceAccess;
        _homes = homes;
        _notifications = notifications;
        _loginService = loginService;
        _client = client;
        _jwt = jwt;
        _cache = cache;
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
        _roleAssignments = new RoleAssignmentService(assignmentRepo, roleRepo, userRepo, groupRepo, mgmtAuth);
    }

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

    /// <summary>Free-text filter for the active tab's list; reset on tab switch.</summary>
    public string EntitySearch { get; set; } = "";

    // -- Actor Permissions --
    public bool IsGlobalAdmin { get; private set; }

    /// <summary>A logged-in actor may always at least reach this page to view their own
    /// profile; the management surfaces (tabs, create, other users) are gated by
    /// <see cref="IsManagementUser"/>.</summary>
    public bool CanAccessPage { get; private set; }

    /// <summary>Whether the actor holds any user/group/role management authority. When
    /// <c>false</c> the page runs in self-service mode: only the actor's own record is
    /// listed and only the personal (Tier-1) fields are editable.</summary>
    public bool IsManagementUser { get; private set; }
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

    /// <summary>Departments in which the actor may create groups (Global only with global authority).</summary>
    public List<Department> AuthorizedDepartmentsForGroupCreate { get; private set; } = [];
    public List<Department> AuthorizedDepartmentsForView { get; private set; } = [];

    /// <summary>Departments the actor may add/remove users from (AssignDepartments); offered in the
    /// user-edit department picker. Unrestricted actors get every department.</summary>
    public List<Department> AuthorizedDepartmentsForAssign { get; private set; } = [];

    /// <summary>Whether the user-edit form should expose the department multi-select at all.</summary>
    public bool CanAssignDepartments { get; private set; }

    /// <summary>All departments for display purposes (group/user detail, selectors).</summary>
    public List<Department> AllDepartments { get; private set; } = [];

    /// <summary>Currently configured password policy, for display in the create-user form.</summary>
    public PasswordPolicy PasswordPolicy { get; private set; } = PasswordPolicy.Default();

    // -- Per-Selection Permissions --
    public bool CanEditSelectedUser { get; private set; }
    public bool CanDeleteSelectedUser { get; private set; }
    public bool CanResetPasswordForSelected { get; private set; }

    /// <summary>The actor's own user id, once the actor context is built.</summary>
    public Guid? ActorUserId => _actorContext?.User.Id;

    /// <summary>Whether the selected record is the actor's own account.</summary>
    public bool IsViewingSelf => SelectedUser is not null
        && _actorContext is not null && SelectedUser.Id == _actorContext.User.Id;

    /// <summary>Whether the actor may open the edit form for the selection: either full
    /// profile authority, or it is their own record (Tier-1 personal fields).</summary>
    public bool CanEditSelectedProfile => CanEditSelectedUser || IsViewingSelf;

    /// <summary>Whether the sign-in toggles (enabled, may change password) are editable: never on
    /// one's own account, so no admin can disable their own account or revoke their own password
    /// change. Leaving the Admins group stays possible while another admin exists (last-admin guard).</summary>
    public bool CanEditSecuritySettingsForSelected => CanEditSelectedUser && !IsViewingSelf;

    /// <summary>Whether the "account enabled" toggle is editable: additionally needs
    /// <see cref="ManagementPermission.EnableDisableUsers"/> on the user.</summary>
    public bool CanEditAccountEnabledForSelected => CanEditSecuritySettingsForSelected && _canEnableDisableSelected;

    /// <summary>File services (app, web, SMB, WebDAV) are sign-in switches too: same rule as
    /// <see cref="CanEditAccountEnabledForSelected"/>.</summary>
    public bool CanEditServiceAccessForSelected => CanEditAccountEnabledForSelected;

    /// <summary>Whether the "may change password" toggle is editable: it belongs to password
    /// management and needs <see cref="ManagementPermission.ResetPasswords"/> on the user.</summary>
    public bool CanEditCanChangePasswordForSelected => CanEditSecuritySettingsForSelected && CanResetPasswordForSelected;

    private bool _canEnableDisableSelected;

    /// <summary>Whether the actor may change the selected user's password from the edit form:
    /// an admin with reset authority, or the user changing their OWN password when the account
    /// permits it (mirrors Windows/AD "user may change own password").</summary>
    public bool CanChangePasswordForSelected => CanResetPasswordForSelected
        || (IsViewingSelf && (SelectedUser?.CanChangePassword ?? false));

    /// <summary>True only for the self-service password path (requires the current password).</summary>
    public bool IsSelfPasswordChange => !CanResetPasswordForSelected && IsViewingSelf;

    // -- Selection --
    public User? SelectedUser { get; set; }
    public Group? SelectedGroup { get; set; }
    public Role? SelectedRole { get; set; }

    // -- Edit-Modus --
    public bool IsEditing { get; private set; }
    public bool IsSaving { get; private set; }

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
    //  Department name resolver (for display)
    // ══════════════════════════════════════════

    public string GetDepartmentName(Guid departmentId)
    {
        if (departmentId == WellKnownGUIDs.DEPARTMENT_GLOBAL) return "Global";
        var dept = AllDepartments.FirstOrDefault(d => d.Id == departmentId);
        return dept?.Name ?? departmentId.ToString();
    }

    /// <summary>
    /// Department name plus its color/soft background, so the group list can render
    /// the same colored chip the share management list uses (see
    /// <see cref="DynamicHelpers.DepartmentPalette"/>).
    /// </summary>
    public DepartmentDisplay GetDepartmentDisplay(Guid departmentId)
    {
        var hex = AllDepartments.FirstOrDefault(d => d.Id == departmentId)?.Color;
        var (color, soft) = DynamicHelpers.DepartmentPalette.For(departmentId, hex);
        return new DepartmentDisplay(GetDepartmentName(departmentId), color, soft);
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

            AllDepartments = (await _departmentRepo.GetAllAsync()).OrderByGlobalFirst().ToList();
            PasswordPolicy = await GetPasswordPolicyAsync();

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

        CanManageHomes = _homes is not null
            && await _homes.GetHomesShareAsync() is { IsEnabled: true }
            && await _mgmtAuth.HasGlobalPermissionAsync(_actorContext, ManagementPermission.ManageHomes);
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

        IsManagementUser = CanCreateUsers || canEditUsers || CanManageGroups || CanManageRoles;
        // Any logged-in actor may reach the page to view (and self-service edit) their own
        // profile; management surfaces are gated separately by IsManagementUser.
        CanAccessPage = true;

        // Resolve authorized departments for user creation
        var createResult = await _mgmtAuth.GetAuthorizedDepartmentIdsAsync(
            _actorContext, ManagementPermission.CreateUsers);
        AuthorizedDepartmentsForCreate = await LoadDepartmentsFromScopeResultAsync(createResult);
        AuthorizedDepartmentsForGroupCreate = await LoadDepartmentsFromScopeResultAsync(
            await _mgmtAuth.GetAuthorizedDepartmentIdsAsync(_actorContext, ManagementPermission.CreateGroups));

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

        var assignResult = await _mgmtAuth.GetAuthorizedDepartmentIdsAsync(
            _actorContext, ManagementPermission.AssignDepartments);
        // Every user implicitly belongs to the Global department, so it is not offered here.
        AuthorizedDepartmentsForAssign = (await LoadDepartmentsFromScopeResultAsync(assignResult))
            .Where(d => d.Id != WellKnownGUIDs.DEPARTMENT_GLOBAL).ToList();
        CanAssignDepartments = AuthorizedDepartmentsForAssign.Count > 0;
    }

    private async Task ResolveSelectedUserPermissionsAsync(User user)
    {
        if (_actorContext == null)
        {
            CanEditSelectedUser = false;
            CanDeleteSelectedUser = false;
            CanResetPasswordForSelected = false;
            _canEnableDisableSelected = false;
            return;
        }

        CanEditSelectedUser = await _mgmtAuth.CanManageUserAsync(
            _actorContext, user.Id, ManagementPermission.EditUserProfiles);
        CanDeleteSelectedUser = await _mgmtAuth.CanManageUserAsync(
            _actorContext, user.Id, ManagementPermission.DeleteUsers);
        CanResetPasswordForSelected = await _mgmtAuth.CanManageUserAsync(
            _actorContext, user.Id, ManagementPermission.ResetPasswords);
        _canEnableDisableSelected = await _mgmtAuth.CanManageUserAsync(
            _actorContext, user.Id, ManagementPermission.EnableDisableUsers);
    }

    private async Task<List<Department>> LoadDepartmentsFromScopeResultAsync(AuthorizedScopeResult result)
    {
        if (result.IsUnrestricted)
            return (await _departmentRepo.GetAllAsync()).OrderByGlobalFirst().ToList();
        if (result.ScopeIds.Count == 0) return [];
        return await LoadDepartmentsByIdsAsync(result.ScopeIds.ToList());
    }

    private async Task<List<Department>> LoadDepartmentsByIdsAsync(List<Guid> ids)
    {
        if (ids.Count == 0) return [];
        var all = await _departmentRepo.GetAllAsync();
        return all.Where(d => ids.Contains(d.Id)).OrderByGlobalFirst().ToList();
    }

    // ══════════════════════════════════════════
    //  Tab Switching
    // ══════════════════════════════════════════

    public async Task SwitchTabAsync(AdminTab tab)
    {
        ActiveTab = tab;
        EntitySearch = "";
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
            _canEnableDisableSelected = false;
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
    //  Helpers
    // ══════════════════════════════════════════

    public void CancelEdit()
    {
        IsEditing = false; IsSaving = false;
        IsConfirmingDelete = false; IsAddingAssignment = false;
        NewPassword = ""; ConfirmPassword = ""; CurrentPassword = "";
        _photoChanged = false;
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
        var selfId = _actorContext?.User.Id;

        var filtered = new List<User>();
        foreach (var user in allUsers)
        {
            // Self is always viewable, even without department-view authority — this also
            // supplies the single row a pure self-service user sees.
            if (user.Id == selfId) { filtered.Add(user); continue; }
            if (authorizedDeptIds.Count == 0) continue;
            var userDepts = await _departmentRepo.GetDepartmentsForUserAsync(user.Id);
            if (userDepts.Any(d => authorizedDeptIds.Contains(d.Id)))
                filtered.Add(user);
        }
        Users = filtered;
    }

    /// <summary>Jumps to the actor's own record for the "view my profile" navigation:
    /// switches to the Users tab first (so a Groups/Roles view does not stay stuck), then
    /// selects the actor if not already selected.</summary>
    public async Task GoToSelfAsync()
    {
        if (_actorContext is null) return;

        if (ActiveTab != AdminTab.Users)
            await SwitchTabAsync(AdminTab.Users);

        var self = Users.FirstOrDefault(u => u.Id == _actorContext.User.Id);
        if (self is not null && SelectedUser?.Id != self.Id)
            await SelectUserAsync(self);
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

}

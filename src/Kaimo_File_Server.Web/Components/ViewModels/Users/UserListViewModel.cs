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
using Kaimo_File_Server.Web.Controllers.WebDav;
using Kaimo_File_Server.Web.Services;
using Microsoft.Extensions.Caching.Memory;

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
    private readonly ILoginService? _loginService;
    private readonly ClientConnectionInfo? _client;
    private readonly JwtTokenService? _jwt;
    private readonly IMemoryCache? _cache;

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
        IMemoryCache? cache = null)
    {
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
    }

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

        var result = await _loginService.AuthenticateAsync(user.Username, CurrentPassword, _client?.RemoteAddress);
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
        await provider.StoreTokenInLocalStorageAsync(token);
    }

    private void InvalidateCachedWebDavLogins(Guid userId)
    {
        if (_cache is not null)
            WebDavBasicAuthenticationHandler.Invalidate(_cache, userId);
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
    public List<Department> AuthorizedDepartmentsForView { get; private set; } = [];

    /// <summary>Departments the actor may add/remove users from (EditDepartment); offered in the
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

    // -- User-Edit --
    public string EditUserName { get; set; } = "";
    public string EditUserDescription { get; set; } = "";
    public string EditUserEmail { get; set; } = "";
    public string EditUserFirstName { get; set; } = "";
    public string EditUserLastName { get; set; } = "";
    public bool EditUserIsEnabled { get; set; } = true;
    public bool EditUserCanChangePassword { get; set; } = true;
    public List<CheckboxItem<Group>> EditUserGroups { get; private set; } = [];
    public List<CheckboxItem<Role>> EditUserRoles { get; private set; } = [];
    public List<CheckboxItem<Department>> EditUserDepartments { get; private set; } = [];

    // -- Profile picture edit (shared by admin full-edit and self-service) --
    /// <summary>Upload cap. Comfortably above the AD <c>thumbnailPhoto</c> ~100 KB norm while
    /// keeping the base64 payload embedded on the page small.</summary>
    public const int MaxPhotoBytes = 256 * 1024;
    public byte[]? EditUserPhoto { get; private set; }
    public string? EditUserPhotoContentType { get; private set; }
    private bool _photoChanged;
    public bool HasEditPhoto => EditUserPhoto is { Length: > 0 };
    public string? EditPhotoDataUri => DynamicHelpers.UserAvatar.DataUri(EditUserPhoto, EditUserPhotoContentType);

    /// <summary>Current password, required when a user changes their OWN password (self-service).</summary>
    public string CurrentPassword { get; set; } = "";

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
    public string EditGroupName { get; set; } = "";
    public List<CheckboxItem<User>> EditGroupMembers { get; private set; } = [];

    /// <summary>Ids of the users already in the edited group's department, captured when the edit
    /// starts. Drives the live "will be added to the department" warning below.</summary>
    private HashSet<Guid> _editGroupDeptMemberIds = [];

    /// <summary>Live warning shown while editing a department-scoped group's members: the selected
    /// users that are not yet in the group's department and so will be added to it on save. <c>null</c>
    /// when the group is Global or every selected member already belongs to the department.</summary>
    public string? EditGroupDeptWarning
    {
        get
        {
            if (SelectedGroup is null || SelectedGroup.DepartmentId == WellKnownGUIDs.DEPARTMENT_GLOBAL)
                return null;
            var missing = EditGroupMembers
                .Where(m => m.IsChecked && !_editGroupDeptMemberIds.Contains(m.Item.Id))
                .Select(m => m.Item.Name)
                .ToList();
            return missing.Count == 0
                ? null
                : string.Format(Resources.Web_Group_DeptWarning,
                    GetDepartmentName(SelectedGroup.DepartmentId), string.Join(", ", missing));
        }
    }

    // -- Role-Edit --
    public string EditRoleName { get; set; } = "";
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

    /// <summary>All shares, offered in the scope selector when assigning a role at Share scope.
    /// A share's GUID is not something an operator knows by heart, so the picker lists shares
    /// by name rather than asking for a raw identifier.</summary>
    public List<ShareDefinition> AllShares { get; private set; } = [];

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
        ]),
        new(Resources.Web_PermGroup_ExternalStorage,
        [
            new(ManagementPermission.ManageCloudAccess, Resources.Web_Perm_ManageCloudAccess),
            new(ManagementPermission.ManageConnections, Resources.Web_Perm_ManageConnections),
            new(ManagementPermission.UseConnections, Resources.Web_Perm_UseConnections),
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
            _actorContext, ManagementPermission.EditDepartment);
        AuthorizedDepartmentsForAssign = await LoadDepartmentsFromScopeResultAsync(assignResult);
        CanAssignDepartments = AuthorizedDepartmentsForAssign.Count > 0;
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

        // Last-admin protection: if this user is currently the SOLE enabled global admin,
        // the save must not strip that — neither by disabling the account nor by unchecking
        // the Administrator role (group-inherited admin survives a role uncheck).
        var adminUserIds = await GetEnabledGlobalAdminUserIdsAsync();
        if (adminUserIds.Contains(SelectedUser.Id) && !adminUserIds.Any(id => id != SelectedUser.Id))
        {
            var keepsAdminRole = EditUserRoles.Any(r => r.IsChecked && IsAdminRole(r.Item));
            var willBeAdmin = EditUserIsEnabled
                && (keepsAdminRole || await UserInheritsGlobalAdminFromGroupAsync(SelectedUser.Id));
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

            var selectedRoleIds = EditUserRoles.Where(r => r.IsChecked).Select(r => r.Item.Id).ToList();
            if (await _mgmtAuth.CanManageUserAsync(_actorContext, SelectedUser.Id, ManagementPermission.AssignRoles))
                await _userRepo.SetRolesForUserAsync(SelectedUser.Id, selectedRoleIds);

            // Admin role <-> Admins group are mirrored. Derive the intended admin state
            // from what was actually persisted (each list respects its own permission
            // gate above), then reconcile both representations.
            var hasAdminRole = (await _userRepo.GetRolesForUserAsync(SelectedUser.Id)).Any(IsAdminRole);
            var inAdminsGroup = (await _userRepo.GetGroupsForUserAsync(SelectedUser.Id))
                .Any(g => g.Id == WellKnownGUIDs.GROUP_ADMINS);
            await ReconcileAdminCouplingAsync(SelectedUser.Id, hasAdminRole || inAdminsGroup);

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

    /// <summary>
    /// Self-service save: persists only the Tier-1 personal fields (given/sur name, photo)
    /// and — when the account permits and the current password checks out — the user's own
    /// password. Never touches identity, security or membership fields.
    /// </summary>
    private async Task SaveSelfProfileAsync()
    {
        if (SelectedUser is null) return;

        try
        {
            IsSaving = true;
            ErrorMessage = null;

            if (!string.IsNullOrWhiteSpace(NewPassword))
            {
                if (!SelectedUser.CanChangePassword)
                { ErrorMessage = Resources.Web_User_NoPermissionPasswordReset; return; }
                var pwError = (await GetPasswordPolicyAsync()).Validate(NewPassword);
                if (pwError is not null) { ErrorMessage = pwError; return; }
                if (NewPassword != ConfirmPassword)
                { ErrorMessage = Resources.Web_User_PasswordsDoNotMatch; return; }
                var currentPasswordError = await VerifyCurrentPasswordAsync(SelectedUser);
                if (currentPasswordError is not null) { ErrorMessage = currentPasswordError; return; }
                await _userRepo.UpdatePasswordAsync(SelectedUser.Id,
                    _passwordService.HashPassword(NewPassword),
                    _ntHashProtector.Protect(_passwordService.ComputeNtHash(NewPassword)),
                    changedByUser: true);
                InvalidateCachedWebDavLogins(SelectedUser.Id);
                // The new security stamp ended every other session; keep this one.
                await RenewOwnSessionAsync(SelectedUser.Id);
            }

            await _userRepo.UpdatePersonalNamesAsync(
                SelectedUser.Id, NullIfBlank(EditUserFirstName), NullIfBlank(EditUserLastName));
            if (_photoChanged)
                await _userRepo.UpdatePhotoAsync(SelectedUser.Id, EditUserPhoto, EditUserPhotoContentType);

            await LoadTabDataAsync();
            SelectedUser = Users.FirstOrDefault(u => u.Id == SelectedUser.Id);
            if (SelectedUser is not null) await ResolveSelectedUserPermissionsAsync(SelectedUser);

            IsEditing = false;
            NewPassword = ""; ConfirmPassword = ""; CurrentPassword = "";
            SuccessMessage = Resources.Web_ChangesSaved;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error saving own profile for {UserId}", SelectedUser?.Id);
            ErrorMessage = Resources.Web_Error_SaveFailed;
        }
        finally { IsSaving = false; }
    }

    // ══════════════════════════════════════════
    //  Profile picture
    // ══════════════════════════════════════════

    /// <summary>Stages an uploaded image for the next save (admin or self-service).</summary>
    public void SetEditPhoto(byte[] bytes, string contentType)
    {
        EditUserPhoto = bytes;
        EditUserPhotoContentType = contentType;
        _photoChanged = true;
    }

    /// <summary>Stages removal of the current photo for the next save.</summary>
    public void ClearEditPhoto()
    {
        EditUserPhoto = null;
        EditUserPhotoContentType = null;
        _photoChanged = true;
    }

    /// <summary>Surfaces a UI-side validation error (e.g. rejected photo) on the shared banner.</summary>
    public void ReportError(string message) => ErrorMessage = message;

    private static string? NullIfBlank(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    // ══════════════════════════════════════════
    //  Edit Group
    // ══════════════════════════════════════════

    /// <summary>Whether the selected group may be edited at all. The Everyone group is a live
    /// mirror of every registered user (membership is maintained automatically from user
    /// creation to deletion), so it is never editable — neither its name nor its members.</summary>
    public bool SelectedGroupIsEditable => SelectedGroup is not null
        && SelectedGroup.Id != WellKnownGUIDs.GROUP_EVERYONE;

    public async Task StartEditGroupAsync()
    {
        if (SelectedGroup is null || _actorContext is null) return;

        if (!SelectedGroupIsEditable)
        {
            ErrorMessage = Resources.Web_Error_EveryoneNotEditable;
            return;
        }

        if (!await _mgmtAuth.CanManageGroupAsync(
                _actorContext, SelectedGroup.Id, ManagementPermission.ManageGroupMembers))
        {
            ErrorMessage = Resources.Web_Error_NoPermission;
            return;
        }

        IsEditing = true;
        ErrorMessage = null; SuccessMessage = null;

        EditGroupName = SelectedGroup.Name;

        var allUsers = (await _userRepo.GetAllAsync()).OrderBy(u => u.Name).ToList();
        var members = await _groupRepo.GetMembersAsync(SelectedGroup.Id);
        var memberIds = members.Select(u => u.Id).ToHashSet();
        EditGroupMembers = allUsers.Select(u => new CheckboxItem<User>(u, memberIds.Contains(u.Id))).ToList();

        // Capture the group's current department members so the edit UI can warn, live, which
        // selected users will be pulled into the department on save (see EditGroupDeptWarning).
        _editGroupDeptMemberIds = SelectedGroup.DepartmentId == WellKnownGUIDs.DEPARTMENT_GLOBAL
            ? []
            : (await _departmentRepo.GetUsersAsync(SelectedGroup.DepartmentId)).Select(u => u.Id).ToHashSet();
    }

    public async Task SaveGroupAsync()
    {
        if (SelectedGroup is null || _actorContext is null) return;

        if (!SelectedGroupIsEditable)
        {
            ErrorMessage = Resources.Web_Error_EveryoneNotEditable;
            return;
        }

        if (!await _mgmtAuth.CanManageGroupAsync(
                _actorContext, SelectedGroup.Id, ManagementPermission.ManageGroupMembers))
        {
            ErrorMessage = Resources.Web_Error_NoPermission;
            return;
        }

        if (string.IsNullOrWhiteSpace(EditGroupName))
        {
            ErrorMessage = Resources.Web_Group_NameRequired;
            return;
        }

        // Admins membership mirrors the global admin role, so emptying it of enabled
        // members would strip the last admin. Block that, mirroring the last-admin guard.
        if (SelectedGroup.Id == WellKnownGUIDs.GROUP_ADMINS
            && !EditGroupMembers.Any(m => m.IsChecked && m.Item.IsEnabled))
        {
            ErrorMessage = Resources.Web_Error_LastAdmin;
            return;
        }

        try
        {
            IsSaving = true;
            ErrorMessage = null;

            // Group.Name is init-only, so a rename is applied by persisting a fresh
            // aggregate carrying the same identity and department (mirrors role rename).
            if (EditGroupName.Trim() != SelectedGroup.Name)
            {
                var renamed = new Group(SelectedGroup.Id, EditGroupName.Trim(), SelectedGroup.DepartmentId);
                await _groupRepo.UpdateAsync(renamed);
                SelectedGroup = renamed;

                var idx = AllGroups.FindIndex(g => g.Id == renamed.Id);
                if (idx >= 0) AllGroups[idx] = renamed;
                AllGroups = AllGroups.OrderBy(g => g.Name).ToList();
            }

            var selectedUserIds = EditGroupMembers.Where(m => m.IsChecked).Select(m => m.Item.Id).ToList();

            if (SelectedGroup.Id == WellKnownGUIDs.GROUP_ADMINS)
            {
                // Full-mirror: every membership change here grants/revokes the admin role.
                var previousMemberIds = (await _groupRepo.GetMembersAsync(SelectedGroup.Id))
                    .Select(u => u.Id).ToHashSet();
                await _groupRepo.SetMembersAsync(SelectedGroup.Id, selectedUserIds);

                foreach (var uid in selectedUserIds)
                    await ReconcileAdminCouplingAsync(uid, true);
                foreach (var uid in previousMemberIds.Where(id => !selectedUserIds.Contains(id)))
                    await ReconcileAdminCouplingAsync(uid, false);
            }
            else
            {
                await _groupRepo.SetMembersAsync(SelectedGroup.Id, selectedUserIds);
            }

            // A member of a department-scoped group must also belong to that department —
            // otherwise the membership is orphaned: it would not surface on the user's own
            // record, and the user's next profile save would silently drop it (the group is
            // filtered out of the department-scoped picker). So grant the group's department
            // to every selected member that lacks it, and surface who was affected.
            var addedToDept = new List<string>();
            if (SelectedGroup.DepartmentId != WellKnownGUIDs.DEPARTMENT_GLOBAL)
            {
                var deptMemberIds = (await _departmentRepo.GetUsersAsync(SelectedGroup.DepartmentId))
                    .Select(u => u.Id).ToHashSet();
                foreach (var member in EditGroupMembers
                             .Where(m => m.IsChecked && !deptMemberIds.Contains(m.Item.Id)))
                {
                    await _departmentRepo.AddUserAsync(SelectedGroup.DepartmentId, member.Item.Id);
                    addedToDept.Add(member.Item.Name);
                }
            }

            GroupMembers = (await _groupRepo.GetMembersAsync(SelectedGroup.Id)).OrderBy(u => u.Name).ToList();

            IsEditing = false;
            SuccessMessage = addedToDept.Count == 0
                ? Resources.Web_Members_Saved
                : $"{Resources.Web_Members_Saved} {string.Format(Resources.Web_Group_MembersAddedToDept, GetDepartmentName(SelectedGroup.DepartmentId), string.Join(", ", addedToDept))}";
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
        AllShares = (await _shareRepo.GetAllAsync()).OrderBy(s => s.Name).ToList();
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

            // Granting the global admin role to a USER also puts them in the Admins group.
            // (A group principal keeps the existing group-inheritance path, untouched.)
            if (NewAssignmentScopeType == ScopeType.Global && IsAdminRole(SelectedRole)
                && await _userRepo.GetByIdAsync(NewAssignmentPrincipalId.Value) is not null)
                await ReconcileAdminCouplingAsync(NewAssignmentPrincipalId.Value, true);

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

        // Last-admin protection: never remove the global assignment that is the sole
        // remaining source of an enabled global administrator.
        if (assignment.ScopeType == ScopeType.Global && role is not null && IsAdminRole(role)
            && (await GetEnabledGlobalAdminUserIdsAsync(excludeAssignmentId: assignmentId)).Count == 0)
        { ErrorMessage = Resources.Web_Error_LastAdmin; return; }

        try
        {
            IsSaving = true;
            ErrorMessage = null;
            await _assignmentRepo.DeleteAsync(assignmentId);

            // Full-mirror: removing a user's global admin role also removes them from Admins.
            if (assignment.ScopeType == ScopeType.Global && role is not null && IsAdminRole(role)
                && await _userRepo.GetByIdAsync(assignment.PrincipalId) is not null)
                await ReconcileAdminCouplingAsync(assignment.PrincipalId, false);

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
            await _departmentRepo.AddUserAsync(CreateUserDepartmentId.Value, user.Id);
            // Every user is a member of the global Everyone group.
            await _groupRepo.AddMemberAsync(WellKnownGUIDs.GROUP_EVERYONE, user.Id);

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

        // Last-admin protection: deleting the user removes their assignments and group
        // memberships too, so refuse if this user is the sole enabled global admin.
        var adminUserIds = await GetEnabledGlobalAdminUserIdsAsync();
        if (adminUserIds.Count == 1 && adminUserIds.Contains(SelectedUser.Id))
        { ErrorMessage = Resources.Web_Error_LastAdmin; return; }

        try
        {
            IsSaving = true;
            await _userRepo.DeleteAsync(SelectedUser.Id);
            InvalidateCachedWebDavLogins(SelectedUser.Id);
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
        if (WellKnownGUIDs.PROTECTED_GROUPS.Contains(SelectedGroup.Id))
        { ErrorMessage = Resources.Web_Error_SystemGroupUndeletable; return; }
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
        var shareLookup = (await _shareRepo.GetAllAsync()).ToDictionary(s => s.Id);

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
                ScopeType.Share => shareLookup.TryGetValue(a.ScopeId, out var share)
                    ? share.Name : a.ScopeId.ToString(),
                _ => a.ScopeId.ToString()
            };

            items.Add(new ScopedAssignmentDisplayItem(
                a.Id, principalName, isGroup, roleName, a.ScopeType, scopeName));
        }

        return items.OrderBy(i => i.PrincipalName).ToList();
    }

    // ══════════════════════════════════════════
    //  Last-Admin Protection
    // ══════════════════════════════════════════

    /// <summary>A role that carries the complete <see cref="ManagementPermission.FullAdmin"/>
    /// mask — the same definition the page uses for <c>IsGlobalAdmin</c>.</summary>
    private static bool IsAdminRole(Role role)
        => (role.ManagementPermissions & ManagementPermission.FullAdmin) == ManagementPermission.FullAdmin;

    /// <summary>
    /// The set of ENABLED users who effectively hold a global FullAdmin role, either
    /// directly or inherited through group membership. This is the authoritative
    /// "who can still administer the system" set that the last-admin guards protect:
    /// no operation may reduce it to empty, or the whole management UI becomes
    /// permanently unreachable.
    /// </summary>
    // ponytail: the guards live in this ViewModel because all write paths that can
    // strip global admin (role uncheck, assignment delete, user delete, user disable)
    // are here. If a second write path appears (planned client-sync REST API), move
    // this helper into ManagementAuthService.
    private async Task<HashSet<Guid>> GetEnabledGlobalAdminUserIdsAsync(Guid? excludeAssignmentId = null)
    {
        var globalAssignments = await _assignmentRepo.GetByScopeAsync(ScopeType.Global, Guid.Empty);
        var roles = (await _roleRepo.GetAllAsync()).ToDictionary(r => r.Id);
        var users = (await _userRepo.GetAllAsync()).ToDictionary(u => u.Id);
        var groupIds = (await _groupRepo.GetAllAsync()).Select(g => g.Id).ToHashSet();

        var admins = new HashSet<Guid>();
        foreach (var a in globalAssignments)
        {
            if (a.Id == excludeAssignmentId) continue;
            if (!roles.TryGetValue(a.RoleId, out var role) || !IsAdminRole(role)) continue;

            if (users.TryGetValue(a.PrincipalId, out var user))
            {
                if (user.IsEnabled) admins.Add(user.Id);
            }
            else if (groupIds.Contains(a.PrincipalId))
            {
                foreach (var member in await _groupRepo.GetMembersAsync(a.PrincipalId))
                    if (member.IsEnabled) admins.Add(member.Id);
            }
        }
        return admins;
    }

    /// <summary>
    /// Keeps the two admin representations of a user in lockstep (full mirror): a direct
    /// global <see cref="WellKnownGUIDs.ROLE_ADMIN"/> assignment and membership in the
    /// Admins group (<see cref="WellKnownGUIDs.GROUP_ADMINS"/>). Both are ensured when
    /// <paramref name="shouldBeAdmin"/> is true and both are removed when false. Idempotent.
    /// Callers are responsible for the last-admin guard before revoking.
    /// </summary>
    private async Task ReconcileAdminCouplingAsync(Guid userId, bool shouldBeAdmin)
    {
        var globalAssignments = await _assignmentRepo.GetByPrincipalAndScopeAsync(
            userId, ScopeType.Global, Guid.Empty);
        var adminAssignment = globalAssignments.FirstOrDefault(a => a.RoleId == WellKnownGUIDs.ROLE_ADMIN);

        if (shouldBeAdmin)
        {
            if (adminAssignment is null)
                await _assignmentRepo.CreateAsync(
                    ScopedRoleAssignment.Global(userId, WellKnownGUIDs.ROLE_ADMIN));
            await _groupRepo.AddMemberAsync(WellKnownGUIDs.GROUP_ADMINS, userId);
        }
        else
        {
            if (adminAssignment is not null)
                await _assignmentRepo.DeleteAsync(adminAssignment.Id);
            await _groupRepo.RemoveMemberAsync(WellKnownGUIDs.GROUP_ADMINS, userId);
        }
    }

    /// <summary>Whether <paramref name="userId"/> inherits a global FullAdmin role through
    /// any group it belongs to. Used to tell whether unchecking the user's OWN admin role
    /// still leaves it an admin (group-inherited authority is untouched by that edit).</summary>
    private async Task<bool> UserInheritsGlobalAdminFromGroupAsync(Guid userId)
    {
        var globalAssignments = await _assignmentRepo.GetByScopeAsync(ScopeType.Global, Guid.Empty);
        var roles = (await _roleRepo.GetAllAsync()).ToDictionary(r => r.Id);
        var groupIds = (await _groupRepo.GetAllAsync()).Select(g => g.Id).ToHashSet();

        foreach (var a in globalAssignments)
        {
            if (!groupIds.Contains(a.PrincipalId)) continue;
            if (!roles.TryGetValue(a.RoleId, out var role) || !IsAdminRole(role)) continue;
            if ((await _groupRepo.GetMembersAsync(a.PrincipalId)).Any(m => m.Id == userId))
                return true;
        }
        return false;
    }
}

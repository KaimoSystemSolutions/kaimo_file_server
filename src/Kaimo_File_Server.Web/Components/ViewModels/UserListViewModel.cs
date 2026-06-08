using Kaimo_File_Server.Core.Domain;
using Kaimo_File_Server.Core.Domain.Department;
using Kaimo_File_Server.Core.Domain.Identity;
using Kaimo_File_Server.Core.Repositories;
using Kaimo_File_Server.Core.Security;
using Kaimo_File_Server.Core.Services;
using Kaimo_File_Server.Web.Helpers;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.Extensions.Logging;

namespace Kaimo_File_Server.Web.Components.ViewModels;

public enum AdminTab { Users, Groups, Roles, Departments }

public class UserListViewModel
{
    private readonly IUserRepository _userRepo;
    private readonly IGroupRepository _groupRepo;
    private readonly IRoleRepository _roleRepo;
    private readonly IDepartmentRepository _departmentRepo;
    private readonly IScopedRoleAssignmentRepository _assignmentRepo;
    private readonly IPasswordService _passwordService;
    private readonly IManagementAuthService _mgmtAuth;
    private readonly IUserContextFactory _userContextFactory;
    private readonly AuthenticationStateProvider _authState;
    private readonly ILogger<UserListViewModel> _logger;
    private readonly IDepartmentPermissionService _deptPermService;
    private readonly IShareRepository _shareRepo;

    public UserListViewModel(
        IUserRepository userRepo,
        IGroupRepository groupRepo,
        IRoleRepository roleRepo,
        IDepartmentRepository departmentRepo,
        IScopedRoleAssignmentRepository assignmentRepo,
        IPasswordService passwordService,
        IManagementAuthService mgmtAuth,
        IUserContextFactory userContextFactory,
        AuthenticationStateProvider authState,
        ILogger<UserListViewModel> logger,
        IDepartmentPermissionService deptPermService,
        IShareRepository shareRepo)
    {
        _userRepo = userRepo;
        _groupRepo = groupRepo;
        _roleRepo = roleRepo;
        _departmentRepo = departmentRepo;
        _assignmentRepo = assignmentRepo;
        _passwordService = passwordService;
        _mgmtAuth = mgmtAuth;
        _userContextFactory = userContextFactory;
        _authState = authState;
        _logger = logger;
        _deptPermService = deptPermService;
        _shareRepo = shareRepo;
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
    public List<Department> Departments { get; private set; } = [];
    public bool IsLoading { get; private set; }
    public string? ErrorMessage { get; private set; }
    public string? SuccessMessage { get; private set; }
    public AdminTab ActiveTab { get; private set; } = AdminTab.Users;

    // ── Actor Permissions (resolved once on load via ManagementAuthService) ──

    /// <summary>
    /// True if the actor has FullAdmin at Global scope.
    /// This is now COMPUTED via ManagementAuthService, not hardcoded role name check.
    /// An Administrator role with FullAdmin permissions will set this to true.
    /// A custom role with FullAdmin scoped to a department will NOT set this to true.
    /// </summary>
    public bool IsGlobalAdmin { get; private set; }

    public bool CanAccessPage { get; private set; }
    public bool CanCreateUsers { get; private set; }
    public bool CanManageGroups { get; private set; }
    public bool CanManageRoles { get; private set; }
    public bool CanManageDepartments { get; private set; }
    public bool CanDeleteUsers { get; private set; }
    public bool CanDeleteGroups { get; private set; }
    public bool CanDeleteRoles { get; private set; }
    public bool CanDeleteDepartments { get; private set; }

    public List<Department> AuthorizedDepartmentsForCreate { get; private set; } = [];
    public List<Department> AuthorizedDepartmentsForView { get; private set; } = [];

    // ── Per-Selection Permissions ──
    public bool CanEditSelectedUser { get; private set; }
    public bool CanDeleteSelectedUser { get; private set; }
    public bool CanResetPasswordForSelected { get; private set; }

    // ── Selection ──
    public User? SelectedUser { get; set; }
    public Group? SelectedGroup { get; set; }
    public Role? SelectedRole { get; set; }
    public Department? SelectedDepartment { get; set; }

    // ── Edit-Modus ──
    public bool IsEditing { get; private set; }
    public bool IsSaving { get; private set; }

    // ── User-Edit ──
    public string EditUserName { get; set; } = "";
    public string EditUserDescription { get; set; } = "";
    public string EditUserEmail { get; set; } = "";
    public bool EditUserIsEnabled { get; set; } = true;
    public bool EditUserCanChangePassword { get; set; } = true;
    public List<CheckboxItem<Group>> EditUserGroups { get; private set; } = [];
    public List<CheckboxItem<Role>> EditUserRoles { get; private set; } = [];

    // ── User-Details ──
    public List<Group> UserGroups { get; private set; } = [];
    public List<Role> UserRoles { get; private set; } = [];
    public List<ScopedAssignmentDisplayItem> UserScopedAssignments { get; private set; } = [];

    // ── Group-Details ──
    public List<User> GroupMembers { get; private set; } = [];
    public List<Role> GroupRoles { get; private set; } = [];

    // ── Role-Details ──
    public List<User> RoleMembers { get; private set; } = [];
    public List<ScopedAssignmentDisplayItem> RoleScopedAssignments { get; private set; } = [];

    // ── Department-Details ──
    public List<User> DepartmentMembers { get; private set; } = [];
    public List<ShareDefinition> DepartmentShares { get; private set; } = [];
    public DepartmentPermissionInfo? DepartmentPermInfo { get; private set; }

    // ── Department-Edit ──
    public string EditDepartmentName { get; set; } = "";
    public List<CheckboxItem<User>> EditDepartmentMembers { get; private set; } = [];
    public Guid? EditDepartmentParentId { get; set; }
    public long? EditDepartmentDefaultPermission { get; set; }
    public bool EditDepartmentHasOwnPermission { get; set; }
    public List<CheckboxItem<ShareDefinition>> EditDepartmentShares { get; private set; } = [];
    public List<Department> AvailableParentDepartments { get; private set; } = [];

    // ── Password-Change ──
    public string NewPassword { get; set; } = "";
    public string ConfirmPassword { get; set; } = "";

    // ── Group-Edit ──
    public List<CheckboxItem<User>> EditGroupMembers { get; private set; } = [];

    // ── Role-Edit ──
    public List<CheckboxItem<User>> EditRoleMembers { get; private set; } = [];
    public ManagementPermission EditRolePermissions { get; set; } = ManagementPermission.None;

    // ── Create User ──
    public bool IsCreatingUser { get; set; }
    public string CreateUserName { get; set; } = "";
    public string CreateUserUsername { get; set; } = "";
    public string CreateUserPassword { get; set; } = "";
    public string CreateUserDescription { get; set; } = "";
    public string CreateUserEmail { get; set; } = "";
    public bool CreateUserIsEnabled { get; set; } = true;
    public bool CreateUserCanChangePassword { get; set; } = true;
    public Guid? CreateUserDepartmentId { get; set; }

    // ── Create Group / Role / Department ──
    public bool IsCreatingGroup { get; set; }
    public string CreateGroupName { get; set; } = "";
    public bool IsCreatingRole { get; set; }
    public string CreateRoleName { get; set; } = "";
    public bool IsCreatingDepartment { get; set; }
    public string CreateDepartmentName { get; set; } = "";

    // ── Delete Confirmation ──
    public bool IsConfirmingDelete { get; set; }

    // ── Scoped Assignment Creation ──
    public bool IsAddingAssignment { get; set; }
    public ScopeType NewAssignmentScopeType { get; set; } = ScopeType.Global;
    public Guid? NewAssignmentScopeId { get; set; }
    public Guid? NewAssignmentPrincipalId { get; set; }
    public bool NewAssignmentPrincipalIsGroup { get; set; }
    public List<Department> AllDepartments { get; private set; } = [];
    public List<User> AllUsers { get; private set; } = [];
    public List<Group> AllGroups { get; private set; } = [];

    // ══════════════════════════════════════════
    //  Computed
    // ══════════════════════════════════════════

    public string TabSubtitle => ActiveTab switch
    {
        AdminTab.Users => $"{Users.Count} Benutzer",
        AdminTab.Groups => $"{Groups.Count} Gruppen",
        AdminTab.Roles => $"{Roles.Count} Rollen",
        AdminTab.Departments => $"{Departments.Count} Abteilungen",
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

    public bool HasDeptPermFlag(long flag)
        => DepartmentPermissionDisplay.HasFlag(EditDepartmentDefaultPermission ?? 0, flag);

    public void ToggleDeptPermFlag(long flag, bool value)
    {
        EditDepartmentDefaultPermission = DepartmentPermissionDisplay.SetFlag(
            EditDepartmentDefaultPermission ?? 0, flag, value);
    }

    public static readonly List<PermissionGroup> PermissionGroups =
    [
        new("Benutzerverwaltung",
        [
            new(ManagementPermission.CreateUsers, "Benutzer erstellen"),
            new(ManagementPermission.DeleteUsers, "Benutzer löschen"),
            new(ManagementPermission.EditUserProfiles, "Profile bearbeiten"),
            new(ManagementPermission.ResetPasswords, "Passwörter zurücksetzen"),
            new(ManagementPermission.EnableDisableUsers, "Aktivieren/Deaktivieren"),
        ]),
        new("Gruppenverwaltung",
        [
            new(ManagementPermission.CreateGroups, "Gruppen erstellen"),
            new(ManagementPermission.DeleteGroups, "Gruppen löschen"),
            new(ManagementPermission.ManageGroupMembers, "Mitglieder verwalten"),
        ]),
        new("Zuweisung & Delegation",
        [
            new(ManagementPermission.AssignGroups, "Gruppen zuweisen"),
            new(ManagementPermission.AssignRoles, "Rollen zuweisen"),
            new(ManagementPermission.AssignDepartments, "Abteilungen zuweisen"),
        ]),
        new("Freigabenverwaltung",
        [
            new(ManagementPermission.CreateShares, "Freigaben erstellen"),
            new(ManagementPermission.DeleteShares, "Freigaben löschen"),
            new(ManagementPermission.EditShareSettings, "Einstellungen bearbeiten"),
            new(ManagementPermission.ManageShareAccess, "Zugriff verwalten"),
            new(ManagementPermission.ManageShareAcls, "ACLs verwalten"),
        ]),
        new("Abteilungsverwaltung",
        [
            new(ManagementPermission.EditDepartment, "Abteilung bearbeiten"),
            new(ManagementPermission.ViewDepartment, "Abteilung anzeigen"),
        ]),
    ];

    public static readonly List<PermissionPreset> PermissionPresets =
    [
        new("UserAdmin", ManagementPermission.UserAdmin),
        new("GroupAdmin", ManagementPermission.GroupAdmin),
        new("ShareAdmin", ManagementPermission.ShareAdmin),
        new("DepartmentAdmin", ManagementPermission.DepartmentAdmin),
        new("FullAdmin", ManagementPermission.FullAdmin),
    ];

    public void ApplyPermissionPreset(ManagementPermission preset)
        => EditRolePermissions = preset;

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
                ErrorMessage = "Nicht angemeldet.";
                return;
            }

            await ResolveActorPermissionsAsync();

            if (!CanAccessPage)
            {
                ErrorMessage = "Keine Berechtigung für die Verwaltung.";
                return;
            }

            await LoadTabDataAsync();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Fehler beim Laden der Benutzerverwaltung");
            ErrorMessage = "Fehler beim Laden.";
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

    /// <summary>
    /// Resolves all actor permissions via ManagementAuthService.
    /// NO hardcoded role name checks — everything goes through the unified pipeline.
    ///
    /// IsGlobalAdmin is now computed: true if actor has FullAdmin at Global scope.
    /// This covers both the system "Administrator" role AND any custom role
    /// that happens to have FullAdmin assigned globally.
    /// </summary>
    private async Task ResolveActorPermissionsAsync()
    {
        if (_actorContext == null) return;

        // ── Check if actor has unrestricted global admin ──
        // This replaces the old: _actorContext.Roles.Any(r => r.Name == "Administrator")
        // Now it works for ANY role with FullAdmin permissions assigned globally.
        IsGlobalAdmin = await _mgmtAuth.HasGlobalPermissionAsync(
            _actorContext, ManagementPermission.FullAdmin);

        // ── Resolve individual permissions ──
        // These all go through ManagementAuthService which now considers
        // both direct role assignments AND scoped assignments.
        CanCreateUsers = await _mgmtAuth.HasAnyPermissionAsync(
            _actorContext, ManagementPermission.CreateUsers);

        var canEditUsers = await _mgmtAuth.HasAnyPermissionAsync(
            _actorContext, ManagementPermission.EditUserProfiles);

        var canViewDepts = await _mgmtAuth.HasAnyPermissionAsync(
            _actorContext, ManagementPermission.ViewDepartment);

        var canEditDepts = await _mgmtAuth.HasAnyPermissionAsync(
            _actorContext, ManagementPermission.EditDepartment);

        CanManageGroups = await _mgmtAuth.HasAnyPermissionAsync(
            _actorContext, ManagementPermission.CreateGroups
                         | ManagementPermission.ManageGroupMembers);

        CanManageRoles = await _mgmtAuth.HasAnyPermissionAsync(
            _actorContext, ManagementPermission.AssignRoles);

        CanManageDepartments = canEditDepts || canViewDepts;

        CanDeleteUsers = await _mgmtAuth.HasAnyPermissionAsync(
            _actorContext, ManagementPermission.DeleteUsers);

        CanDeleteGroups = await _mgmtAuth.HasAnyPermissionAsync(
            _actorContext, ManagementPermission.DeleteGroups);

        CanDeleteRoles = IsGlobalAdmin; // Only global admins can delete roles

        CanDeleteDepartments = IsGlobalAdmin; // Only global admins can delete departments

        CanAccessPage = CanCreateUsers || canEditUsers || CanManageGroups
                      || CanManageRoles || CanManageDepartments;

        // ── Resolve authorized departments ──
        var createResult = await _mgmtAuth.GetAuthorizedDepartmentIdsAsync(
            _actorContext, ManagementPermission.CreateUsers);
        AuthorizedDepartmentsForCreate = await LoadDepartmentsFromScopeResultAsync(createResult);

        var editResult = await _mgmtAuth.GetAuthorizedDepartmentIdsAsync(
            _actorContext, ManagementPermission.EditUserProfiles);
        var viewResult = await _mgmtAuth.GetAuthorizedDepartmentIdsAsync(
            _actorContext, ManagementPermission.ViewDepartment);

        if (editResult.IsUnrestricted || viewResult.IsUnrestricted)
        {
            AuthorizedDepartmentsForView = await _departmentRepo.GetAllAsync();
        }
        else
        {
            var mergedIds = editResult.ScopeIds
                .Concat(viewResult.ScopeIds)
                .Distinct()
                .ToList();
            AuthorizedDepartmentsForView = await LoadDepartmentsByIdsAsync(mergedIds);
        }

        CreateUserDepartmentId = AuthorizedDepartmentsForCreate.FirstOrDefault()?.Id;
    }

    /// <summary>
    /// Resolves per-user permissions for the selected user.
    /// All checks go through ManagementAuthService — no IsGlobalAdmin shortcut needed
    /// because the Admin role's Global-scoped FullAdmin will pass all checks anyway.
    /// </summary>
    private async Task ResolveSelectedUserPermissionsAsync(User user)
    {
        if (_actorContext == null)
        {
            CanEditSelectedUser = false;
            CanDeleteSelectedUser = false;
            CanResetPasswordForSelected = false;
            return;
        }

        // ManagementAuthService now handles Global-scoped admin roles automatically
        CanEditSelectedUser = await _mgmtAuth.CanManageUserAsync(
            _actorContext, user.Id, ManagementPermission.EditUserProfiles);

        CanDeleteSelectedUser = await _mgmtAuth.CanManageUserAsync(
            _actorContext, user.Id, ManagementPermission.DeleteUsers);

        CanResetPasswordForSelected = await _mgmtAuth.CanManageUserAsync(
            _actorContext, user.Id, ManagementPermission.ResetPasswords);
    }

    private async Task<List<Department>> LoadDepartmentsFromScopeResultAsync(
        AuthorizedScopeResult result)
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
        CancelEdit();
        CancelCreate();
        SelectedUser = null; SelectedGroup = null;
        SelectedRole = null; SelectedDepartment = null;
        ErrorMessage = null; SuccessMessage = null;

        try
        {
            IsLoading = true;
            await LoadTabDataAsync();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Fehler beim Tab-Wechsel zu {Tab}", tab);
            ErrorMessage = "Fehler beim Laden.";
        }
        finally { IsLoading = false; }
    }

    // ══════════════════════════════════════════
    //  Selection (async)
    // ══════════════════════════════════════════

    public async Task SelectUserAsync(User user)
    {
        CancelEdit(); CancelCreate();
        SelectedGroup = null; SelectedRole = null; SelectedDepartment = null;
        SuccessMessage = null;

        if (SelectedUser?.Id == user.Id)
        {
            SelectedUser = null;
            UserRoles = []; UserGroups = []; UserScopedAssignments = [];
            CanEditSelectedUser = false;
            CanDeleteSelectedUser = false;
            CanResetPasswordForSelected = false;
            return;
        }

        SelectedUser = user;
        await ResolveSelectedUserPermissionsAsync(user);
        UserRoles = (await _userRepo.GetRolesForUserAsync(user.Id)).OrderBy(r => r.Name).ToList();
        UserGroups = (await _userRepo.GetGroupsForUserAsync(user.Id)).OrderBy(g => g.Name).ToList();
        await LoadUserScopedAssignmentsAsync(user.Id);
    }

    public async Task SelectGroupAsync(Group group)
    {
        CancelEdit(); CancelCreate();
        SelectedUser = null; SelectedRole = null; SelectedDepartment = null;
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
        SelectedUser = null; SelectedGroup = null; SelectedDepartment = null;
        SuccessMessage = null;

        if (SelectedRole?.Id == role.Id)
        {
            SelectedRole = null;
            RoleMembers = []; RoleScopedAssignments = [];
            return;
        }

        SelectedRole = role;
        RoleMembers = (await _roleRepo.GetMembersAsync(role.Id)).OrderBy(u => u.Name).ToList();
        await LoadRoleScopedAssignmentsAsync(role.Id);
    }

    public async Task SelectDepartmentAsync(Department department)
    {
        CancelEdit(); CancelCreate();
        SelectedUser = null; SelectedGroup = null; SelectedRole = null;
        SuccessMessage = null;

        if (SelectedDepartment?.Id == department.Id)
        {
            SelectedDepartment = null;
            DepartmentMembers = []; DepartmentShares = [];
            DepartmentPermInfo = null;
            return;
        }

        SelectedDepartment = department;
        DepartmentMembers = (await _departmentRepo.GetUsersAsync(department.Id))
            .OrderBy(u => u.Name).ToList();
        DepartmentShares = (await _departmentRepo.GetSharesAsync(department.Id))
            .OrderBy(s => s.Name).ToList();
        DepartmentPermInfo = await _deptPermService.GetPermissionInfoAsync(department.Id);
    }

    // ══════════════════════════════════════════
    //  Edit User
    // ══════════════════════════════════════════

    public async Task StartEditUserAsync()
    {
        if (SelectedUser is null || _actorContext is null) return;

        // Permission check via service — no IsGlobalAdmin shortcut needed
        if (!await _mgmtAuth.CanManageUserAsync(
                _actorContext, SelectedUser.Id, ManagementPermission.EditUserProfiles))
        {
            ErrorMessage = "Keine Berechtigung, diesen Benutzer zu bearbeiten.";
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

        var allGroups = (await _groupRepo.GetAllAsync()).OrderBy(g => g.Name).ToList();
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

        // All permission checks go through ManagementAuthService
        if (!await _mgmtAuth.CanManageUserAsync(
                _actorContext, SelectedUser.Id, ManagementPermission.EditUserProfiles))
        {
            ErrorMessage = "Keine Berechtigung, diesen Benutzer zu bearbeiten.";
            return;
        }

        try
        {
            IsSaving = true;
            ErrorMessage = null;

            if (EditUserName != SelectedUser.Name && !string.IsNullOrWhiteSpace(EditUserName))
                await _userRepo.UpdateNameAsync(SelectedUser.Id, EditUserName.Trim());

            await _userRepo.UpdateProfileAsync(
                SelectedUser.Id,
                EditUserDescription.Trim(),
                EditUserEmail.Trim(),
                EditUserIsEnabled,
                EditUserCanChangePassword);

            // Password reset — check specific permission
            if (!string.IsNullOrWhiteSpace(NewPassword))
            {
                if (!await _mgmtAuth.CanManageUserAsync(
                        _actorContext, SelectedUser.Id, ManagementPermission.ResetPasswords))
                {
                    ErrorMessage = "Keine Berechtigung für Passwort-Reset.";
                    return;
                }

                if (NewPassword.Length < 6) { ErrorMessage = "Passwort muss mindestens 6 Zeichen lang sein."; return; }
                if (NewPassword != ConfirmPassword) { ErrorMessage = "Passwörter stimmen nicht überein."; return; }
                var hash = _passwordService.HashPassword(NewPassword);
                var ntHash = _passwordService.ComputeNtHash(NewPassword);
                await _userRepo.UpdatePasswordAsync(SelectedUser.Id, hash, ntHash);
            }

            // Group assignment — check specific permission
            var selectedGroupIds = EditUserGroups.Where(g => g.IsChecked).Select(g => g.Item.Id).ToList();
            if (await _mgmtAuth.CanManageUserAsync(
                    _actorContext, SelectedUser.Id, ManagementPermission.AssignGroups))
            {
                await _userRepo.SetGroupsForUserAsync(SelectedUser.Id, selectedGroupIds);
            }

            // Role assignment — check specific permission
            var selectedRoleIds = EditUserRoles.Where(r => r.IsChecked).Select(r => r.Item.Id).ToList();
            if (await _mgmtAuth.CanManageUserAsync(
                    _actorContext, SelectedUser.Id, ManagementPermission.AssignRoles))
            {
                await _userRepo.SetRolesForUserAsync(SelectedUser.Id, selectedRoleIds);
            }

            await LoadTabDataAsync();

            SelectedUser = Users.FirstOrDefault(u => u.Id == SelectedUser.Id);
            if (SelectedUser is not null)
            {
                await ResolveSelectedUserPermissionsAsync(SelectedUser);
                UserRoles = (await _userRepo.GetRolesForUserAsync(SelectedUser.Id)).OrderBy(r => r.Name).ToList();
                UserGroups = (await _userRepo.GetGroupsForUserAsync(SelectedUser.Id)).OrderBy(g => g.Name).ToList();
                await LoadUserScopedAssignmentsAsync(SelectedUser.Id);
            }

            IsEditing = false;
            NewPassword = ""; ConfirmPassword = "";
            SuccessMessage = "Änderungen gespeichert.";
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Fehler beim Speichern von Benutzer {UserId}", SelectedUser.Id);
            ErrorMessage = "Fehler beim Speichern.";
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
            ErrorMessage = "Keine Berechtigung, diese Gruppe zu bearbeiten.";
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
            ErrorMessage = "Keine Berechtigung, diese Gruppe zu bearbeiten.";
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
            SuccessMessage = "Mitglieder gespeichert.";
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Fehler beim Speichern der Gruppe {GroupId}", SelectedGroup.Id);
            ErrorMessage = "Fehler beim Speichern.";
        }
        finally { IsSaving = false; }
    }

    // ══════════════════════════════════════════
    //  Edit Role
    // ══════════════════════════════════════════

    public async Task StartEditRoleAsync()
    {
        if (SelectedRole is null || _actorContext is null) return;

        if (!await _mgmtAuth.HasAnyPermissionAsync(
                _actorContext, ManagementPermission.AssignRoles))
        {
            ErrorMessage = "Keine Berechtigung, Rollen zu bearbeiten.";
            return;
        }

        IsEditing = true;
        ErrorMessage = null; SuccessMessage = null;

        var allUsers = (await _userRepo.GetAllAsync()).OrderBy(u => u.Name).ToList();
        var members = await _roleRepo.GetMembersAsync(SelectedRole.Id);
        var memberIds = members.Select(u => u.Id).ToHashSet();
        EditRoleMembers = allUsers.Select(u => new CheckboxItem<User>(u, memberIds.Contains(u.Id))).ToList();

        EditRolePermissions = SelectedRole.ManagementPermissions;
    }

    public async Task SaveRoleAsync()
    {
        if (SelectedRole is null) return;

        try
        {
            IsSaving = true;
            ErrorMessage = null;

            var selectedUserIds = EditRoleMembers.Where(m => m.IsChecked).Select(m => m.Item.Id).ToList();
            await _roleRepo.SetMembersAsync(SelectedRole.Id, selectedUserIds);

            // System roles: permissions are immutable (managed by SystemRoleSeeder)
            if (!SelectedRole.IsSystemRole)
            {
                SelectedRole.ManagementPermissions = EditRolePermissions;
                await _roleRepo.UpdateAsync(SelectedRole);
            }

            RoleMembers = (await _roleRepo.GetMembersAsync(SelectedRole.Id)).OrderBy(u => u.Name).ToList();

            var refreshed = await _roleRepo.GetByIdAsync(SelectedRole.Id);
            if (refreshed is not null) SelectedRole = refreshed;

            await LoadRoleScopedAssignmentsAsync(SelectedRole.Id);

            IsEditing = false;
            SuccessMessage = "Rolle gespeichert.";
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Fehler beim Speichern der Rolle {RoleId}", SelectedRole.Id);
            ErrorMessage = "Fehler beim Speichern.";
        }
        finally { IsSaving = false; }
    }

    // ══════════════════════════════════════════
    //  Edit Department
    // ══════════════════════════════════════════

    public async Task StartEditDepartmentAsync()
    {
        if (SelectedDepartment is null || _actorContext is null) return;

        if (!await _mgmtAuth.HasAnyPermissionAsync(
                _actorContext, ManagementPermission.EditDepartment))
        {
            ErrorMessage = "Keine Berechtigung, diese Abteilung zu bearbeiten.";
            return;
        }

        IsEditing = true;
        ErrorMessage = null; SuccessMessage = null;

        EditDepartmentName = SelectedDepartment.Name;
        EditDepartmentParentId = SelectedDepartment.ParentDepartmentId;

        var allDepts = await _departmentRepo.GetAllAsync();
        var descendantIds = await _departmentRepo.GetDescendantIdsAsync(SelectedDepartment.Id);
        descendantIds.Add(SelectedDepartment.Id);
        AvailableParentDepartments = allDepts
            .Where(d => !descendantIds.Contains(d.Id))
            .OrderBy(d => d.Name).ToList();

        EditDepartmentHasOwnPermission = SelectedDepartment.DefaultFilePermission.HasValue;
        EditDepartmentDefaultPermission = SelectedDepartment.DefaultFilePermission ?? 0;

        var allUsers = (await _userRepo.GetAllAsync()).OrderBy(u => u.Name).ToList();
        var members = await _departmentRepo.GetUsersAsync(SelectedDepartment.Id);
        var memberIds = members.Select(u => u.Id).ToHashSet();
        EditDepartmentMembers = allUsers
            .Select(u => new CheckboxItem<User>(u, memberIds.Contains(u.Id))).ToList();

        var allShares = (await _shareRepo.GetAllAsync()).OrderBy(s => s.Name).ToList();
        var deptShares = await _departmentRepo.GetSharesAsync(SelectedDepartment.Id);
        var deptShareIds = deptShares.Select(s => s.Id).ToHashSet();
        EditDepartmentShares = allShares
            .Select(s => new CheckboxItem<ShareDefinition>(s, deptShareIds.Contains(s.Id)))
            .ToList();
    }

    public async Task SaveDepartmentAsync()
    {
        if (SelectedDepartment is null || _actorContext is null) return;

        if (!await _mgmtAuth.HasAnyPermissionAsync(
                _actorContext, ManagementPermission.EditDepartment))
        {
            ErrorMessage = "Keine Berechtigung, diese Abteilung zu bearbeiten.";
            return;
        }

        try
        {
            IsSaving = true;
            ErrorMessage = null;

            if (!string.IsNullOrWhiteSpace(EditDepartmentName)
                && EditDepartmentName.Trim() != SelectedDepartment.Name)
                SelectedDepartment.Name = EditDepartmentName.Trim();

            SelectedDepartment.ParentDepartmentId = EditDepartmentParentId;
            SelectedDepartment.DefaultFilePermission = EditDepartmentHasOwnPermission
                ? EditDepartmentDefaultPermission
                : null;

            await _departmentRepo.UpdateAsync(SelectedDepartment);

            // Members diff
            var desiredUserIds = EditDepartmentMembers
                .Where(m => m.IsChecked).Select(m => m.Item.Id).ToHashSet();
            var currentMembers = await _departmentRepo.GetUsersAsync(SelectedDepartment.Id);
            var currentUserIds = currentMembers.Select(u => u.Id).ToHashSet();

            foreach (var userId in desiredUserIds.Except(currentUserIds))
                await _departmentRepo.AddUserAsync(SelectedDepartment.Id, userId);
            foreach (var userId in currentUserIds.Except(desiredUserIds))
                await _departmentRepo.RemoveUserAsync(SelectedDepartment.Id, userId);

            // Shares diff
            var desiredShareIds = EditDepartmentShares
                .Where(s => s.IsChecked).Select(s => s.Item.Id).ToHashSet();
            var currentShares = await _departmentRepo.GetSharesAsync(SelectedDepartment.Id);
            var currentShareIds = currentShares.Select(s => s.Id).ToHashSet();

            foreach (var shareId in desiredShareIds.Except(currentShareIds))
                await _departmentRepo.AddShareAsync(SelectedDepartment.Id, shareId);
            foreach (var shareId in currentShareIds.Except(desiredShareIds))
                await _departmentRepo.RemoveShareAsync(SelectedDepartment.Id, shareId);

            // Refresh
            DepartmentMembers = (await _departmentRepo.GetUsersAsync(SelectedDepartment.Id))
                .OrderBy(u => u.Name).ToList();
            DepartmentShares = (await _departmentRepo.GetSharesAsync(SelectedDepartment.Id))
                .OrderBy(s => s.Name).ToList();

            var refreshed = await _departmentRepo.GetByIdAsync(SelectedDepartment.Id);
            if (refreshed is not null) SelectedDepartment = refreshed;

            DepartmentPermInfo = await _deptPermService.GetPermissionInfoAsync(SelectedDepartment.Id);

            await LoadTabDataAsync();

            IsEditing = false;
            SuccessMessage = "Abteilung gespeichert.";
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Fehler beim Speichern der Abteilung {DeptId}", SelectedDepartment.Id);
            ErrorMessage = "Fehler beim Speichern.";
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
        AllDepartments = await _departmentRepo.GetAllAsync();
        AllUsers = (await _userRepo.GetAllAsync()).OrderBy(u => u.Name).ToList();
        AllGroups = (await _groupRepo.GetAllAsync()).OrderBy(g => g.Name).ToList();
    }

    public async Task CreateAssignmentAsync()
    {
        if (SelectedRole is null || _actorContext is null) return;

        if (NewAssignmentPrincipalId is null)
        { ErrorMessage = "Bitte einen Benutzer oder eine Gruppe auswählen."; return; }

        if (NewAssignmentScopeType != ScopeType.Global && NewAssignmentScopeId is null)
        { ErrorMessage = "Bitte einen Geltungsbereich auswählen."; return; }

        if (!await _mgmtAuth.HasAnyPermissionAsync(
                _actorContext, ManagementPermission.AssignRoles))
        {
            ErrorMessage = "Keine Berechtigung, Rollenzuweisungen zu erstellen.";
            return;
        }

        try
        {
            IsSaving = true;
            ErrorMessage = null;

            var scopeId = NewAssignmentScopeType == ScopeType.Global
                ? Guid.Empty : NewAssignmentScopeId!.Value;

            var assignment = new ScopedRoleAssignment(
                NewAssignmentPrincipalId.Value,
                SelectedRole.Id,
                NewAssignmentScopeType,
                scopeId);

            await _assignmentRepo.CreateAsync(assignment);

            IsAddingAssignment = false;
            await LoadRoleScopedAssignmentsAsync(SelectedRole.Id);
            SuccessMessage = "Zuweisung erstellt.";
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Fehler beim Erstellen der Zuweisung");
            ErrorMessage = "Fehler beim Erstellen der Zuweisung.";
        }
        finally { IsSaving = false; }
    }

    public async Task DeleteAssignmentAsync(Guid assignmentId)
    {
        if (_actorContext is null) return;

        if (!await _mgmtAuth.HasAnyPermissionAsync(
                _actorContext, ManagementPermission.AssignRoles))
        {
            ErrorMessage = "Keine Berechtigung, Zuweisungen zu entfernen.";
            return;
        }

        try
        {
            IsSaving = true;
            ErrorMessage = null;
            await _assignmentRepo.DeleteAsync(assignmentId);

            if (SelectedRole is not null) await LoadRoleScopedAssignmentsAsync(SelectedRole.Id);
            if (SelectedUser is not null) await LoadUserScopedAssignmentsAsync(SelectedUser.Id);

            SuccessMessage = "Zuweisung entfernt.";
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Fehler beim Löschen der Zuweisung {Id}", assignmentId);
            ErrorMessage = "Fehler beim Löschen der Zuweisung.";
        }
        finally { IsSaving = false; }
    }

    // ══════════════════════════════════════════
    //  Create User
    // ══════════════════════════════════════════

    public void StartCreateUser()
    {
        if (!CanCreateUsers)
        { ErrorMessage = "Keine Berechtigung, Benutzer zu erstellen."; return; }

        CancelEdit();
        SelectedUser = null; SelectedGroup = null; SelectedRole = null; SelectedDepartment = null;
        IsCreatingUser = true; IsCreatingGroup = false; IsCreatingRole = false; IsCreatingDepartment = false;
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

        if (string.IsNullOrWhiteSpace(CreateUserUsername)) { ErrorMessage = "Benutzername erforderlich."; return; }
        if (string.IsNullOrWhiteSpace(CreateUserName)) { ErrorMessage = "Name erforderlich."; return; }
        if (string.IsNullOrWhiteSpace(CreateUserPassword) || CreateUserPassword.Length < 6)
        { ErrorMessage = "Passwort muss mindestens 6 Zeichen lang sein."; return; }
        if (CreateUserDepartmentId is null) { ErrorMessage = "Bitte eine Abteilung auswählen."; return; }

        // Scoped check: can user create in THIS specific department?
        if (!await _mgmtAuth.CanCreateUserInDepartmentAsync(
                _actorContext, CreateUserDepartmentId.Value))
        {
            ErrorMessage = "Keine Berechtigung, in dieser Abteilung Benutzer zu erstellen.";
            return;
        }

        try
        {
            IsSaving = true;

            var existing = await _userRepo.GetByUsernameAsync(CreateUserUsername.Trim());
            if (existing is not null) { ErrorMessage = "Benutzername bereits vergeben."; return; }

            var hash = _passwordService.HashPassword(CreateUserPassword);
            var ntHash = _passwordService.ComputeNtHash(CreateUserPassword);
            var user = new User(
                Guid.NewGuid(), CreateUserName.Trim(), CreateUserUsername.Trim(),
                hash, ntHash,
                description: CreateUserDescription.Trim(), email: CreateUserEmail.Trim(),
                isEnabled: CreateUserIsEnabled, canChangePassword: CreateUserCanChangePassword);

            await _userRepo.CreateAsync(user);
            await _departmentRepo.AddUserAsync(CreateUserDepartmentId.Value, user.Id);

            IsCreatingUser = false;
            CreateUserName = ""; CreateUserUsername = ""; CreateUserPassword = "";
            CreateUserDescription = ""; CreateUserEmail = "";
            CreateUserIsEnabled = true; CreateUserCanChangePassword = true;
            await LoadTabDataAsync();
            SuccessMessage = "Benutzer erstellt.";
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Fehler beim Erstellen des Benutzers");
            ErrorMessage = "Fehler beim Erstellen.";
        }
        finally { IsSaving = false; }
    }

    // ══════════════════════════════════════════
    //  Create Group / Role / Department
    // ══════════════════════════════════════════

    public void StartCreateGroup()
    {
        if (!CanManageGroups) { ErrorMessage = "Keine Berechtigung."; return; }
        CancelEdit();
        SelectedUser = null; SelectedGroup = null; SelectedRole = null; SelectedDepartment = null;
        IsCreatingGroup = true; IsCreatingUser = false; IsCreatingRole = false; IsCreatingDepartment = false;
        CreateGroupName = "";
        ErrorMessage = null; SuccessMessage = null;
    }

    public async Task CreateGroupAsync()
    {
        ErrorMessage = null;
        if (string.IsNullOrWhiteSpace(CreateGroupName)) { ErrorMessage = "Gruppenname erforderlich."; return; }
        if (_actorContext is null) return;

        if (!await _mgmtAuth.HasAnyPermissionAsync(_actorContext, ManagementPermission.CreateGroups))
        { ErrorMessage = "Keine Berechtigung."; return; }

        try
        {
            IsSaving = true;
            var group = new Group(Guid.NewGuid(), CreateGroupName.Trim());
            await _groupRepo.CreateAsync(group);
            IsCreatingGroup = false; CreateGroupName = "";
            await LoadTabDataAsync();
            SuccessMessage = "Gruppe erstellt.";
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Fehler beim Erstellen der Gruppe");
            ErrorMessage = "Fehler beim Erstellen.";
        }
        finally { IsSaving = false; }
    }

    public void StartCreateRole()
    {
        if (!CanManageRoles) { ErrorMessage = "Keine Berechtigung."; return; }
        CancelEdit();
        SelectedUser = null; SelectedGroup = null; SelectedRole = null; SelectedDepartment = null;
        IsCreatingRole = true; IsCreatingUser = false; IsCreatingGroup = false; IsCreatingDepartment = false;
        CreateRoleName = "";
        ErrorMessage = null; SuccessMessage = null;
    }

    public async Task CreateRoleAsync()
    {
        ErrorMessage = null;
        if (string.IsNullOrWhiteSpace(CreateRoleName)) { ErrorMessage = "Rollenname erforderlich."; return; }
        if (_actorContext is null) return;

        if (!await _mgmtAuth.HasAnyPermissionAsync(_actorContext, ManagementPermission.AssignRoles))
        { ErrorMessage = "Keine Berechtigung."; return; }

        try
        {
            IsSaving = true;
            var role = new Role(Guid.NewGuid(), CreateRoleName.Trim());
            await _roleRepo.CreateAsync(role);
            IsCreatingRole = false; CreateRoleName = "";
            await LoadTabDataAsync();
            SuccessMessage = "Rolle erstellt.";
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Fehler beim Erstellen der Rolle");
            ErrorMessage = "Fehler beim Erstellen.";
        }
        finally { IsSaving = false; }
    }

    public void StartCreateDepartment()
    {
        if (!CanManageDepartments) { ErrorMessage = "Keine Berechtigung."; return; }
        CancelEdit();
        SelectedUser = null; SelectedGroup = null; SelectedRole = null; SelectedDepartment = null;
        IsCreatingDepartment = true; IsCreatingUser = false; IsCreatingGroup = false; IsCreatingRole = false;
        CreateDepartmentName = "";
        ErrorMessage = null; SuccessMessage = null;
    }

    public async Task CreateDepartmentAsync()
    {
        ErrorMessage = null;
        if (string.IsNullOrWhiteSpace(CreateDepartmentName)) { ErrorMessage = "Abteilungsname erforderlich."; return; }
        if (_actorContext is null) return;

        if (!await _mgmtAuth.HasAnyPermissionAsync(_actorContext, ManagementPermission.EditDepartment))
        { ErrorMessage = "Keine Berechtigung."; return; }

        try
        {
            IsSaving = true;
            var department = new Department(CreateDepartmentName.Trim());
            await _departmentRepo.CreateAsync(department);
            IsCreatingDepartment = false; CreateDepartmentName = "";
            await LoadTabDataAsync();
            SuccessMessage = "Abteilung erstellt.";
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Fehler beim Erstellen der Abteilung");
            ErrorMessage = "Fehler beim Erstellen.";
        }
        finally { IsSaving = false; }
    }

    // ══════════════════════════════════════════
    //  Delete
    // ══════════════════════════════════════════

    public void RequestDeleteUser() { IsConfirmingDelete = true; ErrorMessage = null; }
    public void RequestDeleteGroup() { IsConfirmingDelete = true; ErrorMessage = null; }
    public void RequestDeleteRole() { IsConfirmingDelete = true; ErrorMessage = null; }
    public void RequestDeleteDepartment() { IsConfirmingDelete = true; ErrorMessage = null; }

    public async Task ConfirmDeleteUserAsync()
    {
        if (SelectedUser is null || _actorContext is null) return;

        if (!await _mgmtAuth.CanManageUserAsync(
                _actorContext, SelectedUser.Id, ManagementPermission.DeleteUsers))
        { ErrorMessage = "Keine Berechtigung."; return; }

        try
        {
            IsSaving = true;
            await _userRepo.DeleteAsync(SelectedUser.Id);
            SelectedUser = null; IsConfirmingDelete = false;
            await LoadTabDataAsync();
            SuccessMessage = "Benutzer gelöscht.";
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Fehler beim Löschen des Benutzers");
            ErrorMessage = "Fehler beim Löschen.";
        }
        finally { IsSaving = false; }
    }

    public async Task ConfirmDeleteGroupAsync()
    {
        if (SelectedGroup is null || _actorContext is null) return;

        if (!await _mgmtAuth.CanManageGroupAsync(
                _actorContext, SelectedGroup.Id, ManagementPermission.DeleteGroups))
        { ErrorMessage = "Keine Berechtigung."; return; }

        try
        {
            IsSaving = true;
            await _groupRepo.DeleteAsync(SelectedGroup.Id);
            SelectedGroup = null; IsConfirmingDelete = false;
            await LoadTabDataAsync();
            SuccessMessage = "Gruppe gelöscht.";
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Fehler beim Löschen der Gruppe");
            ErrorMessage = "Fehler beim Löschen.";
        }
        finally { IsSaving = false; }
    }

    public async Task ConfirmDeleteRoleAsync()
    {
        if (SelectedRole is null || _actorContext is null) return;

        if (!await _mgmtAuth.HasAnyPermissionAsync(
                _actorContext, ManagementPermission.AssignRoles))
        { ErrorMessage = "Keine Berechtigung."; return; }

        try
        {
            IsSaving = true;
            await _roleRepo.DeleteAsync(SelectedRole.Id);
            SelectedRole = null; RoleMembers = []; RoleScopedAssignments = [];
            IsConfirmingDelete = false;
            await LoadTabDataAsync();
            SuccessMessage = "Rolle gelöscht.";
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Fehler beim Löschen der Rolle");
            ErrorMessage = "Fehler beim Löschen.";
        }
        finally { IsSaving = false; }
    }

    public async Task ConfirmDeleteDepartmentAsync()
    {
        if (SelectedDepartment is null || _actorContext is null) return;

        if (!await _mgmtAuth.HasAnyPermissionAsync(
                _actorContext, ManagementPermission.EditDepartment))
        { ErrorMessage = "Keine Berechtigung."; return; }

        try
        {
            IsSaving = true;
            await _departmentRepo.DeleteAsync(SelectedDepartment.Id);
            SelectedDepartment = null; DepartmentMembers = []; DepartmentShares = [];
            DepartmentPermInfo = null; IsConfirmingDelete = false;
            await LoadTabDataAsync();
            SuccessMessage = "Abteilung gelöscht.";
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Fehler beim Löschen der Abteilung");
            ErrorMessage = "Fehler beim Löschen.";
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
        IsCreatingUser = false; IsCreatingGroup = false;
        IsCreatingRole = false; IsCreatingDepartment = false;
        IsConfirmingDelete = false;
        CreateUserName = ""; CreateUserUsername = ""; CreateUserPassword = "";
        CreateUserDescription = ""; CreateUserEmail = "";
        CreateUserIsEnabled = true; CreateUserCanChangePassword = true;
        CreateGroupName = ""; CreateRoleName = ""; CreateDepartmentName = "";
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
            case AdminTab.Departments: await LoadFilteredDepartmentsAsync(); break;
        }
    }

    private async Task LoadFilteredUsersAsync()
    {
        var allUsers = (await _userRepo.GetAllAsync()).OrderBy(u => u.Name).ToList();

        if (IsGlobalAdmin)
        {
            Users = allUsers;
            return;
        }

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
        if (!CanManageGroups) { Groups = []; return; }

        var authorizedDeptIds = AuthorizedDepartmentsForView.Select(d => d.Id).ToHashSet();
        if (authorizedDeptIds.Count == 0) { Groups = allGroups; return; }

        var filtered = new List<Group>();
        foreach (var group in allGroups)
        {
            var groupDepts = await _departmentRepo.GetDepartmentsForGroupAsync(group.Id);
            if (groupDepts.Count == 0 || groupDepts.Any(d => authorizedDeptIds.Contains(d.Id)))
                filtered.Add(group);
        }

        Groups = filtered;
    }

    private async Task LoadFilteredDepartmentsAsync()
    {
        if (IsGlobalAdmin)
        {
            Departments = (await _departmentRepo.GetAllAsync()).OrderBy(d => d.Name).ToList();
            return;
        }

        Departments = AuthorizedDepartmentsForView.OrderBy(d => d.Name).ToList();
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
        var departments = await _departmentRepo.GetAllAsync();
        var deptLookup = departments.ToDictionary(d => d.Id);
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

    public async Task GetUserRoles(User user)
    {
        UserRoles = await _userRepo.GetRolesForUserAsync(user.Id);
    }
}


// ══════════════════════════════════════════
//  Helper Classes
// ══════════════════════════════════════════

public class CheckboxItem<T>
{
    public T Item { get; }
    public bool IsChecked { get; set; }
    public CheckboxItem(T item, bool isChecked) { Item = item; IsChecked = isChecked; }
}

/// <summary>Display-friendly version of a ScopedRoleAssignment.</summary>
public record ScopedAssignmentDisplayItem(
    Guid AssignmentId,
    string PrincipalName,
    bool IsGroup,
    string RoleName,
    ScopeType ScopeType,
    string ScopeName);

/// <summary>A group of related permission flags for UI display.</summary>
public record PermissionGroup(string Label, List<PermissionFlag> Flags);

/// <summary>A single permission flag for checkbox binding.</summary>
public record PermissionFlag(ManagementPermission Flag, string Label);

/// <summary>A named permission preset (shortcut).</summary>
public record PermissionPreset(string Label, ManagementPermission Permissions);
using Kaimo_File_Server.Core.Domain.Department;
using Kaimo_File_Server.Core.Domain.Identity;
using Kaimo_File_Server.Core.Repositories;
using Kaimo_File_Server.Core.Security;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.Extensions.Logging;

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
    private readonly AuthenticationStateProvider _authState;
    private readonly ILogger<UserListViewModel> _logger;

    public UserListViewModel(
        IUserRepository userRepo,
        IGroupRepository groupRepo,
        IRoleRepository roleRepo,
        IDepartmentRepository departmentRepo,
        IScopedRoleAssignmentRepository assignmentRepo,
        IPasswordService passwordService,
        AuthenticationStateProvider authState,
        ILogger<UserListViewModel> logger)
    {
        _userRepo = userRepo;
        _groupRepo = groupRepo;
        _roleRepo = roleRepo;
        _departmentRepo = departmentRepo;
        _assignmentRepo = assignmentRepo;
        _passwordService = passwordService;
        _authState = authState;
        _logger = logger;
    }

    // ══════════════════════════════════════════
    //  State
    // ══════════════════════════════════════════

    public List<User> Users { get; private set; } = [];
    public List<Group> Groups { get; private set; } = [];
    public List<Role> Roles { get; private set; } = [];
    public bool IsLoading { get; private set; }
    public string? ErrorMessage { get; private set; }
    public string? SuccessMessage { get; private set; }
    public bool IsAdmin { get; private set; }
    public bool CanManageUsers { get; private set; }
    public AdminTab ActiveTab { get; private set; } = AdminTab.Users;

    // ── Selection ──
    public User? SelectedUser { get; set; }
    public Group? SelectedGroup { get; set; }
    public Role? SelectedRole { get; set; }

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

    // ── Create Group ──
    public bool IsCreatingGroup { get; set; }
    public string CreateGroupName { get; set; } = "";

    // ── Create Role ──
    public bool IsCreatingRole { get; set; }
    public string CreateRoleName { get; set; } = "";

    // ── Delete Confirmation ──
    public bool IsConfirmingDelete { get; set; }

    // ══════════════════════════════════════════
    //  Scoped Assignment Creation
    // ══════════════════════════════════════════

    public bool IsAddingAssignment { get; set; }
    public ScopeType NewAssignmentScopeType { get; set; } = ScopeType.Global;
    public Guid? NewAssignmentScopeId { get; set; }
    public Guid? NewAssignmentPrincipalId { get; set; }
    public bool NewAssignmentPrincipalIsGroup { get; set; }

    /// <summary>Available departments for scope selection.</summary>
    public List<Department> AllDepartments { get; private set; } = [];

    /// <summary>Available users for principal selection.</summary>
    public List<User> AllUsers { get; private set; } = [];

    /// <summary>Available groups for principal selection.</summary>
    public List<Group> AllGroups { get; private set; } = [];

    // ══════════════════════════════════════════
    //  Computed
    // ══════════════════════════════════════════

    public string TabSubtitle => ActiveTab switch
    {
        AdminTab.Users => $"{Users.Count} Benutzer",
        AdminTab.Groups => $"{Groups.Count} Gruppen",
        AdminTab.Roles => $"{Roles.Count} Rollen",
        _ => ""
    };

    // ══════════════════════════════════════════
    //  Permission Helpers (for UI binding)
    // ══════════════════════════════════════════

    public bool HasEditPermission(ManagementPermission flag)
        => (EditRolePermissions & flag) == flag;

    public void ToggleEditPermission(ManagementPermission flag, bool value)
    {
        if (value)
            EditRolePermissions |= flag;
        else
            EditRolePermissions &= ~flag;
    }

    /// <summary>
    /// All individual permission flags grouped for display.
    /// </summary>
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

    /// <summary>
    /// Shortcut presets for quick assignment.
    /// </summary>
    public static readonly List<PermissionPreset> PermissionPresets =
    [
        new("UserAdmin", ManagementPermission.UserAdmin),
        new("GroupAdmin", ManagementPermission.GroupAdmin),
        new("ShareAdmin", ManagementPermission.ShareAdmin),
        new("DepartmentAdmin", ManagementPermission.DepartmentAdmin),
        new("FullAdmin", ManagementPermission.FullAdmin),
    ];

    public void ApplyPermissionPreset(ManagementPermission preset)
    {
        EditRolePermissions = preset;
    }

    // ══════════════════════════════════════════
    //  Commands
    // ══════════════════════════════════════════

    public async Task LoadAsync()
    {
        try
        {
            IsLoading = true;
            ErrorMessage = null;

            var state = await _authState.GetAuthenticationStateAsync();
            IsAdmin = state.User.IsInRole("Administrator");
            CanManageUsers = IsAdmin || state.User.IsInRole("UserManager");

            if (!CanManageUsers) { ErrorMessage = "Keine Berechtigung."; return; }

            await LoadTabDataAsync();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Fehler beim Laden der Benutzerverwaltung");
            ErrorMessage = "Fehler beim Laden.";
        }
        finally { IsLoading = false; }
    }

    public async Task SwitchTabAsync(AdminTab tab)
    {
        ActiveTab = tab;
        CancelEdit();
        CancelCreate();
        SelectedUser = null;
        SelectedGroup = null;
        SelectedRole = null;
        ErrorMessage = null;
        SuccessMessage = null;

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

    // ── Selection (sync, no detail loading) ──

    public void SelectUser(User user)
    {
        CancelEdit(); CancelCreate();
        SelectedUser = SelectedUser?.Id == user.Id ? null : user;
        SelectedGroup = null; SelectedRole = null;
        SuccessMessage = null;
    }

    public void SelectGroup(Group group)
    {
        CancelEdit(); CancelCreate();
        SelectedGroup = SelectedGroup?.Id == group.Id ? null : group;
        SelectedUser = null; SelectedRole = null;
        SuccessMessage = null;
    }

    public void SelectRole(Role role)
    {
        CancelEdit(); CancelCreate();
        SelectedRole = SelectedRole?.Id == role.Id ? null : role;
        SelectedUser = null; SelectedGroup = null;
        SuccessMessage = null;
    }

    // ── Selection (async, with detail loading) ──

    public async Task SelectUserAsync(User user)
    {
        CancelEdit(); CancelCreate();
        SelectedGroup = null; SelectedRole = null;
        SuccessMessage = null;

        if (SelectedUser?.Id == user.Id)
        {
            SelectedUser = null;
            UserRoles = []; UserGroups = []; UserScopedAssignments = [];
            return;
        }

        SelectedUser = user;
        UserRoles = (await _userRepo.GetRolesForUserAsync(user.Id)).OrderBy(r => r.Name).ToList();
        UserGroups = (await _userRepo.GetGroupsForUserAsync(user.Id)).OrderBy(g => g.Name).ToList();
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
            RoleMembers = []; RoleScopedAssignments = [];
            return;
        }

        SelectedRole = role;
        RoleMembers = (await _roleRepo.GetMembersAsync(role.Id)).OrderBy(u => u.Name).ToList();
        await LoadRoleScopedAssignmentsAsync(role.Id);
    }

    public async Task GetUserRoles(User user)
    {
        var userRoles = await _userRepo.GetRolesForUserAsync(user.Id);
        UserRoles = userRoles;
    }

    // ══════════════════════════════════════════
    //  Edit User
    // ══════════════════════════════════════════

    public async Task StartEditUserAsync()
    {
        if (SelectedUser is null) return;
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
        if (SelectedUser is null) return;

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

            if (!string.IsNullOrWhiteSpace(NewPassword))
            {
                if (NewPassword.Length < 6) { ErrorMessage = "Passwort muss mindestens 6 Zeichen lang sein."; return; }
                if (NewPassword != ConfirmPassword) { ErrorMessage = "Passwörter stimmen nicht überein."; return; }
                var hash = _passwordService.HashPassword(NewPassword);
                var ntHash = _passwordService.ComputeNtHash(NewPassword);
                await _userRepo.UpdatePasswordAsync(SelectedUser.Id, hash, ntHash);
                _logger.LogInformation("Passwort geändert für Benutzer {UserId}", SelectedUser.Id);
            }

            var selectedGroupIds = EditUserGroups.Where(g => g.IsChecked).Select(g => g.Item.Id).ToList();
            await _userRepo.SetGroupsForUserAsync(SelectedUser.Id, selectedGroupIds);

            var selectedRoleIds = EditUserRoles.Where(r => r.IsChecked).Select(r => r.Item.Id).ToList();
            await _userRepo.SetRolesForUserAsync(SelectedUser.Id, selectedRoleIds);

            await LoadTabDataAsync();

            SelectedUser = Users.FirstOrDefault(u => u.Id == SelectedUser.Id);
            if (SelectedUser is not null)
            {
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
        if (SelectedGroup is null) return;
        IsEditing = true;
        ErrorMessage = null; SuccessMessage = null;

        var allUsers = (await _userRepo.GetAllAsync()).OrderBy(u => u.Name).ToList();
        var members = await _groupRepo.GetMembersAsync(SelectedGroup.Id);
        var memberIds = members.Select(u => u.Id).ToHashSet();
        EditGroupMembers = allUsers.Select(u => new CheckboxItem<User>(u, memberIds.Contains(u.Id))).ToList();
    }

    public async Task SaveGroupAsync()
    {
        if (SelectedGroup is null) return;

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
    //  Edit Role (Members + Permissions)
    // ══════════════════════════════════════════

    public async Task StartEditRoleAsync()
    {
        if (SelectedRole is null) return;
        IsEditing = true;
        ErrorMessage = null; SuccessMessage = null;

        var allUsers = (await _userRepo.GetAllAsync()).OrderBy(u => u.Name).ToList();
        var members = await _roleRepo.GetMembersAsync(SelectedRole.Id);
        var memberIds = members.Select(u => u.Id).ToHashSet();
        EditRoleMembers = allUsers.Select(u => new CheckboxItem<User>(u, memberIds.Contains(u.Id))).ToList();

        // Load current permissions for editing
        EditRolePermissions = SelectedRole.ManagementPermissions;
    }

    public async Task SaveRoleAsync()
    {
        if (SelectedRole is null) return;

        try
        {
            IsSaving = true;
            ErrorMessage = null;

            // Save members
            var selectedUserIds = EditRoleMembers.Where(m => m.IsChecked).Select(m => m.Item.Id).ToList();
            await _roleRepo.SetMembersAsync(SelectedRole.Id, selectedUserIds);

            // Save permissions (only for non-system roles)
            if (!SelectedRole.IsSystemRole)
            {
                SelectedRole.ManagementPermissions = EditRolePermissions;
                await _roleRepo.UpdateAsync(SelectedRole);
            }

            // Reload details
            RoleMembers = (await _roleRepo.GetMembersAsync(SelectedRole.Id)).OrderBy(u => u.Name).ToList();

            // Re-fetch role to reflect saved state
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
        if (SelectedRole is null) return;

        if (NewAssignmentPrincipalId is null)
        {
            ErrorMessage = "Bitte einen Benutzer oder eine Gruppe auswählen.";
            return;
        }

        if (NewAssignmentScopeType != ScopeType.Global && NewAssignmentScopeId is null)
        {
            ErrorMessage = "Bitte einen Geltungsbereich auswählen.";
            return;
        }

        try
        {
            IsSaving = true;
            ErrorMessage = null;

            var scopeId = NewAssignmentScopeType == ScopeType.Global
                ? Guid.Empty
                : NewAssignmentScopeId!.Value;

            var assignment = new ScopedRoleAssignment(
                NewAssignmentPrincipalId.Value,
                SelectedRole.Id,
                NewAssignmentScopeType,
                scopeId);

            await _assignmentRepo.CreateAsync(assignment);
            _logger.LogInformation(
                "ScopedRoleAssignment erstellt: Principal={PrincipalId}, Role={RoleId}, Scope={ScopeType}:{ScopeId}",
                assignment.PrincipalId, assignment.RoleId, assignment.ScopeType, assignment.ScopeId);

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
        try
        {
            IsSaving = true;
            ErrorMessage = null;

            await _assignmentRepo.DeleteAsync(assignmentId);
            _logger.LogInformation("ScopedRoleAssignment {Id} gelöscht", assignmentId);

            if (SelectedRole is not null)
                await LoadRoleScopedAssignmentsAsync(SelectedRole.Id);

            if (SelectedUser is not null)
                await LoadUserScopedAssignmentsAsync(SelectedUser.Id);

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
        CancelEdit();
        SelectedUser = null; SelectedGroup = null; SelectedRole = null;
        IsCreatingUser = true; IsCreatingGroup = false; IsCreatingRole = false;
        CreateUserName = ""; CreateUserUsername = ""; CreateUserPassword = "";
        CreateUserDescription = ""; CreateUserEmail = "";
        CreateUserIsEnabled = true; CreateUserCanChangePassword = true;
        ErrorMessage = null; SuccessMessage = null;
    }

    public async Task CreateUserAsync()
    {
        ErrorMessage = null;

        if (string.IsNullOrWhiteSpace(CreateUserUsername)) { ErrorMessage = "Benutzername erforderlich."; return; }
        if (string.IsNullOrWhiteSpace(CreateUserName)) { ErrorMessage = "Name erforderlich."; return; }
        if (string.IsNullOrWhiteSpace(CreateUserPassword) || CreateUserPassword.Length < 6)
        { ErrorMessage = "Passwort muss mindestens 6 Zeichen lang sein."; return; }

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
            _logger.LogInformation("Benutzer '{Username}' erstellt", user.Username);

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
    //  Create Group
    // ══════════════════════════════════════════

    public void StartCreateGroup()
    {
        CancelEdit();
        SelectedUser = null; SelectedGroup = null; SelectedRole = null;
        IsCreatingGroup = true; IsCreatingUser = false; IsCreatingRole = false;
        CreateGroupName = "";
        ErrorMessage = null; SuccessMessage = null;
    }

    public async Task CreateGroupAsync()
    {
        ErrorMessage = null;
        if (string.IsNullOrWhiteSpace(CreateGroupName)) { ErrorMessage = "Gruppenname erforderlich."; return; }

        try
        {
            IsSaving = true;
            var group = new Group(Guid.NewGuid(), CreateGroupName.Trim());
            await _groupRepo.CreateAsync(group);
            _logger.LogInformation("Gruppe '{GroupName}' erstellt", group.Name);

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

    // ══════════════════════════════════════════
    //  Create Role
    // ══════════════════════════════════════════

    public void StartCreateRole()
    {
        CancelEdit();
        SelectedUser = null; SelectedGroup = null; SelectedRole = null;
        IsCreatingRole = true; IsCreatingUser = false; IsCreatingGroup = false;
        CreateRoleName = "";
        ErrorMessage = null; SuccessMessage = null;
    }

    public async Task CreateRoleAsync()
    {
        ErrorMessage = null;
        if (string.IsNullOrWhiteSpace(CreateRoleName)) { ErrorMessage = "Rollenname erforderlich."; return; }

        try
        {
            IsSaving = true;
            var role = new Role(Guid.NewGuid(), CreateRoleName.Trim());
            await _roleRepo.CreateAsync(role);
            _logger.LogInformation("Rolle '{RoleName}' erstellt", role.Name);

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

    // ══════════════════════════════════════════
    //  Delete User / Group / Role
    // ══════════════════════════════════════════

    public void RequestDeleteUser() { IsConfirmingDelete = true; ErrorMessage = null; }
    public void RequestDeleteGroup() { IsConfirmingDelete = true; ErrorMessage = null; }
    public void RequestDeleteRole() { IsConfirmingDelete = true; ErrorMessage = null; }

    public async Task ConfirmDeleteUserAsync()
    {
        if (SelectedUser is null) return;
        try
        {
            IsSaving = true;
            await _userRepo.DeleteAsync(SelectedUser.Id);
            _logger.LogInformation("Benutzer '{Username}' gelöscht", SelectedUser.Username);
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
        if (SelectedGroup is null) return;
        try
        {
            IsSaving = true;
            await _groupRepo.DeleteAsync(SelectedGroup.Id);
            _logger.LogInformation("Gruppe '{GroupName}' gelöscht", SelectedGroup.Name);
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
        if (SelectedRole is null) return;
        try
        {
            IsSaving = true;
            await _roleRepo.DeleteAsync(SelectedRole.Id);
            _logger.LogInformation("Rolle '{RoleName}' gelöscht", SelectedRole.Name);
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

    // ══════════════════════════════════════════
    //  Helpers
    // ══════════════════════════════════════════

    public void CancelEdit()
    {
        IsEditing = false;
        IsSaving = false;
        IsConfirmingDelete = false;
        IsAddingAssignment = false;
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
            case AdminTab.Users:
                Users = (await _userRepo.GetAllAsync()).OrderBy(u => u.Name).ToList();
                break;
            case AdminTab.Groups:
                Groups = (await _groupRepo.GetAllAsync()).OrderBy(g => g.Name).ToList();
                break;
            case AdminTab.Roles:
                Roles = (await _roleRepo.GetAllAsync()).OrderBy(r => r.Name).ToList();
                break;
        }
    }

    /// <summary>
    /// Load all scoped assignments that reference this role,
    /// resolved into display-friendly items.
    /// </summary>
    private async Task LoadRoleScopedAssignmentsAsync(Guid roleId)
    {
        // Get all assignments system-wide, then filter by role
        // (There's no GetByRoleAsync, so we load all users/groups and filter)
        var allAssignments = new List<ScopedRoleAssignment>();

        // Load via all users
        var users = await _userRepo.GetAllAsync();
        foreach (var user in users)
        {
            var assignments = await _assignmentRepo.GetByPrincipalAsync(user.Id);
            allAssignments.AddRange(assignments.Where(a => a.RoleId == roleId));
        }

        // Load via all groups
        var groups = await _groupRepo.GetAllAsync();
        foreach (var group in groups)
        {
            var assignments = await _assignmentRepo.GetByPrincipalAsync(group.Id);
            allAssignments.AddRange(assignments.Where(a => a.RoleId == roleId));
        }

        // Deduplicate by ID
        allAssignments = allAssignments.DistinctBy(a => a.Id).ToList();

        RoleScopedAssignments = await ResolveScopedAssignmentsAsync(allAssignments, users, groups);
    }

    /// <summary>
    /// Load all scoped assignments for a specific user (as principal).
    /// </summary>
    private async Task LoadUserScopedAssignmentsAsync(Guid userId)
    {
        var assignments = await _assignmentRepo.GetByPrincipalAsync(userId);
        var users = await _userRepo.GetAllAsync();
        var groups = await _groupRepo.GetAllAsync();
        UserScopedAssignments = await ResolveScopedAssignmentsAsync(assignments, users, groups);
    }

    /// <summary>
    /// Resolve raw assignments into display items with human-readable names.
    /// </summary>
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
                    ? group.Name
                    : a.PrincipalId.ToString();

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
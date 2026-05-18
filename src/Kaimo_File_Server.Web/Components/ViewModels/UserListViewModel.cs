using Kaimo_File_Server.Core.Domain.Identity;
using Kaimo_File_Server.Core.Repositories;
using Kaimo_File_Server.Core.Security;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.Extensions.Logging;
using System.Collections.Specialized;
using System.ComponentModel;

namespace Kaimo_File_Server.Web.Components.ViewModels;

public enum AdminTab { Users, Groups, Roles }

public class UserListViewModel
{
    private readonly IUserRepository _userRepo;
    private readonly IGroupRepository _groupRepo;
    private readonly IRoleRepository _roleRepo;
    private readonly IPasswordService _passwordService;
    private readonly AuthenticationStateProvider _authState;
    private readonly ILogger<UserListViewModel> _logger;

    public UserListViewModel(
        IUserRepository userRepo,
        IGroupRepository groupRepo,
        IRoleRepository roleRepo,
        IPasswordService passwordService,
        AuthenticationStateProvider authState,
        ILogger<UserListViewModel> logger)
    {
        _userRepo = userRepo;
        _groupRepo = groupRepo;
        _roleRepo = roleRepo;
        _passwordService = passwordService;
        _authState = authState;
        _logger = logger;
    }

    // ── State ──
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

    // User-Edit
    public string EditUserName { get; set; } = "";
    public List<CheckboxItem<Group>> EditUserGroups { get; private set; } = [];
    public List<CheckboxItem<Role>> EditUserRoles { get; private set; } = [];

    // -- User-Details --

    public List<Role> UserRoles { get; private set; } = [];

    // Password-Change
    public string NewPassword { get; set; } = "";
    public string ConfirmPassword { get; set; } = "";

    // Group-Edit
    public List<CheckboxItem<User>> EditGroupMembers { get; private set; } = [];

    // ── Create User ──
    public bool IsCreatingUser { get; set; }
    public string CreateUserName { get; set; } = "";
    public string CreateUserUsername { get; set; } = "";
    public string CreateUserPassword { get; set; } = "";

    // ── Create Group ──
    public bool IsCreatingGroup { get; set; }
    public string CreateGroupName { get; set; } = "";

    // ── Delete Confirmation ──
    public bool IsConfirmingDelete { get; set; }

    // ── Computed ──
    public string TabSubtitle => ActiveTab switch
    {
        AdminTab.Users => $"{Users.Count} Benutzer",
        AdminTab.Groups => $"{Groups.Count} Gruppen",
        AdminTab.Roles => $"{Roles.Count} Rollen",
        _ => ""
    };

    // ── Commands ──

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

    public void SelectUser(User user)
    {
        CancelEdit();
        CancelCreate();
        SelectedUser = SelectedUser?.Id == user.Id ? null : user;
        SelectedGroup = null;
        SelectedRole = null;
        SuccessMessage = null;
    }

    public void SelectGroup(Group group)
    {
        CancelEdit();
        CancelCreate();
        SelectedGroup = SelectedGroup?.Id == group.Id ? null : group;
        SelectedUser = null;
        SelectedRole = null;
        SuccessMessage = null;
    }

    public void SelectRole(Role role)
    {
        CancelEdit();
        CancelCreate();
        SelectedRole = SelectedRole?.Id == role.Id ? null : role;
        SelectedUser = null;
        SelectedGroup = null;
        SuccessMessage = null;
    }

    // -- Show Details --

    public async Task GetUserRoles(User user)
    {
        var allRoles = (await _roleRepo.GetAllAsync()).OrderBy(r => r.Name).ToList();
        var userRoles = await _userRepo.GetRolesForUserAsync(user.Id);
        UserRoles = userRoles;
        //var userRoleIds = userRoles.Select(r => r.Id).ToHashSet();
        //EditUserRoles = allRoles.Select(r => new CheckboxItem<Role>(r, userRoleIds.Contains(r.Id))).ToList();
    }

    // -- Edit User --

    public async Task StartEditUserAsync()
    {
        if (SelectedUser is null) return;
        IsEditing = true;
        ErrorMessage = null;
        SuccessMessage = null;
        NewPassword = "";
        ConfirmPassword = "";

        EditUserName = SelectedUser.Name;

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

            // Name aktualisieren
            if (EditUserName != SelectedUser.Name && !string.IsNullOrWhiteSpace(EditUserName))
            {
                await _userRepo.UpdateNameAsync(SelectedUser.Id, EditUserName.Trim());
            }

            // Passwort ändern (wenn ausgefüllt)
            if (!string.IsNullOrWhiteSpace(NewPassword))
            {
                if (NewPassword.Length < 6)
                {
                    ErrorMessage = "Passwort muss mindestens 6 Zeichen lang sein.";
                    return;
                }
                if (NewPassword != ConfirmPassword)
                {
                    ErrorMessage = "Passwörter stimmen nicht überein.";
                    return;
                }
                var hash = _passwordService.HashPassword(NewPassword);
                var ntHash = _passwordService.ComputeNtHash(NewPassword);
                await _userRepo.UpdatePasswordAsync(SelectedUser.Id, hash, ntHash);
                _logger.LogInformation("Passwort geändert für Benutzer {UserId}", SelectedUser.Id);
            }

            // Gruppen aktualisieren
            var selectedGroupIds = EditUserGroups.Where(g => g.IsChecked).Select(g => g.Item.Id).ToList();
            await _userRepo.SetGroupsForUserAsync(SelectedUser.Id, selectedGroupIds);

            // Rollen aktualisieren
            var selectedRoleIds = EditUserRoles.Where(r => r.IsChecked).Select(r => r.Item.Id).ToList();
            await _userRepo.SetRolesForUserAsync(SelectedUser.Id, selectedRoleIds);

            await LoadTabDataAsync();

            SelectedUser = Users.FirstOrDefault(u => u.Id == SelectedUser.Id);
            IsEditing = false;
            NewPassword = "";
            ConfirmPassword = "";
            SuccessMessage = "Änderungen gespeichert.";
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Fehler beim Speichern von Benutzer {UserId}", SelectedUser.Id);
            ErrorMessage = "Fehler beim Speichern.";
        }
        finally { IsSaving = false; }
    }

    // ── Edit Group ──

    public async Task StartEditGroupAsync()
    {
        if (SelectedGroup is null) return;
        IsEditing = true;
        ErrorMessage = null;
        SuccessMessage = null;

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

    // ── Create User ──

    public void StartCreateUser()
    {
        CancelEdit();
        SelectedUser = null;
        SelectedGroup = null;
        SelectedRole = null;
        IsCreatingUser = true;
        IsCreatingGroup = false;
        CreateUserName = "";
        CreateUserUsername = "";
        CreateUserPassword = "";
        ErrorMessage = null;
        SuccessMessage = null;
    }

    public async Task CreateUserAsync()
    {
        ErrorMessage = null;

        if (string.IsNullOrWhiteSpace(CreateUserUsername))
        {
            ErrorMessage = "Benutzername erforderlich.";
            return;
        }

        if (string.IsNullOrWhiteSpace(CreateUserName))
        {
            ErrorMessage = "Name erforderlich.";
            return;
        }

        if (string.IsNullOrWhiteSpace(CreateUserPassword) || CreateUserPassword.Length < 6)
        {
            ErrorMessage = "Passwort muss mindestens 6 Zeichen lang sein.";
            return;
        }

        try
        {
            IsSaving = true;

            var existing = await _userRepo.GetByUsernameAsync(CreateUserUsername.Trim());
            if (existing is not null)
            {
                ErrorMessage = "Benutzername bereits vergeben.";
                return;
            }

            var hash = _passwordService.HashPassword(CreateUserPassword);
            var ntHash = _passwordService.ComputeNtHash(CreateUserPassword);
            var user = new User(
                Guid.NewGuid(),
                CreateUserName.Trim(),
                CreateUserUsername.Trim(),
                hash,
                ntHash
            );

            await _userRepo.CreateAsync(user);
            _logger.LogInformation("Benutzer '{Username}' erstellt", user.Username);

            IsCreatingUser = false;
            CreateUserName = "";
            CreateUserUsername = "";
            CreateUserPassword = "";
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

    // ── Create Group ──

    public void StartCreateGroup()
    {
        CancelEdit();
        SelectedUser = null;
        SelectedGroup = null;
        SelectedRole = null;
        IsCreatingGroup = true;
        IsCreatingUser = false;
        CreateGroupName = "";
        ErrorMessage = null;
        SuccessMessage = null;
    }

    public async Task CreateGroupAsync()
    {
        ErrorMessage = null;

        if (string.IsNullOrWhiteSpace(CreateGroupName))
        {
            ErrorMessage = "Gruppenname erforderlich.";
            return;
        }

        try
        {
            IsSaving = true;

            var group = new Group(Guid.NewGuid(), CreateGroupName.Trim());
            await _groupRepo.CreateAsync(group);
            _logger.LogInformation("Gruppe '{GroupName}' erstellt", group.Name);

            IsCreatingGroup = false;
            CreateGroupName = "";
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

    // ── Delete User ──

    public void RequestDeleteUser()
    {
        IsConfirmingDelete = true;
        ErrorMessage = null;
    }

    public async Task ConfirmDeleteUserAsync()
    {
        if (SelectedUser is null) return;

        try
        {
            IsSaving = true;
            await _userRepo.DeleteAsync(SelectedUser.Id);
            _logger.LogInformation("Benutzer '{Username}' gelöscht", SelectedUser.Username);

            SelectedUser = null;
            IsConfirmingDelete = false;
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

    // ── Delete Group ──

    public void RequestDeleteGroup()
    {
        IsConfirmingDelete = true;
        ErrorMessage = null;
    }

    public async Task ConfirmDeleteGroupAsync()
    {
        if (SelectedGroup is null) return;

        try
        {
            IsSaving = true;
            await _groupRepo.DeleteAsync(SelectedGroup.Id);
            _logger.LogInformation("Gruppe '{GroupName}' gelöscht", SelectedGroup.Name);

            SelectedGroup = null;
            IsConfirmingDelete = false;
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

    // ── Helpers ──

    public void CancelEdit()
    {
        IsEditing = false;
        IsSaving = false;
        IsConfirmingDelete = false;
        NewPassword = "";
        ConfirmPassword = "";
        ErrorMessage = null;
    }

    public void CancelCreate()
    {
        IsCreatingUser = false;
        IsCreatingGroup = false;
        IsConfirmingDelete = false;
        CreateUserName = "";
        CreateUserUsername = "";
        CreateUserPassword = "";
        CreateGroupName = "";
        ErrorMessage = null;
    }

    private async Task LoadTabDataAsync()
    {
        switch (ActiveTab)
        {
            case AdminTab.Users:
                var users = await _userRepo.GetAllAsync();
                Users = users.OrderBy(u => u.Name).ToList();
                break;
            case AdminTab.Groups:
                var groups = await _groupRepo.GetAllAsync();
                Groups = groups.OrderBy(g => g.Name).ToList();
                break;
            case AdminTab.Roles:
                var roles = await _roleRepo.GetAllAsync();
                Roles = roles.OrderBy(r => r.Name).ToList();
                break;
        }
    }
}

// ── Hilfsklasse für Checkbox-Listen ──
public class CheckboxItem<T>
{
    public T Item { get; }
    public bool IsChecked { get; set; }

    public CheckboxItem(T item, bool isChecked)
    {
        Item = item;
        IsChecked = isChecked;
    }
}
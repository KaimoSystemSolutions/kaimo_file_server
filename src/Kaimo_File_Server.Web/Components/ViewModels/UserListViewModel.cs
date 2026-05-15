using Kaimo_File_Server.Core.Domain.Identity;
using Kaimo_File_Server.Core.Repositories;
using Microsoft.AspNetCore.Components.Authorization;

namespace Kaimo_File_Server.Web.Components.ViewModels;

public enum AdminTab { Users, Groups, Roles }

public class UserListViewModel
{
    private readonly IUserRepository _userRepo;
    private readonly IGroupRepository _groupRepo;
    private readonly IRoleRepository _roleRepo;
    private readonly AuthenticationStateProvider _authState;

    public UserListViewModel(
        IUserRepository userRepo,
        IGroupRepository groupRepo,
        IRoleRepository roleRepo,
        AuthenticationStateProvider authState)
    {
        _userRepo = userRepo;
        _groupRepo = groupRepo;
        _roleRepo = roleRepo;
        _authState = authState;
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

    // Group-Edit
    public List<CheckboxItem<User>> EditGroupMembers { get; private set; } = [];

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
        catch (Exception ex) { ErrorMessage = $"Fehler beim Laden: {ex.Message}"; }
        finally { IsLoading = false; }
    }

    public async Task SwitchTabAsync(AdminTab tab)
    {
        ActiveTab = tab;
        CancelEdit();
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
        catch (Exception ex) { ErrorMessage = $"Fehler beim Laden: {ex.Message}"; }
        finally { IsLoading = false; }
    }

    public void SelectUser(User user)
    {
        CancelEdit();
        SelectedUser = SelectedUser?.Id == user.Id ? null : user;
        SelectedGroup = null;
        SelectedRole = null;
        SuccessMessage = null;
    }

    public void SelectGroup(Group group)
    {
        CancelEdit();
        SelectedGroup = SelectedGroup?.Id == group.Id ? null : group;
        SelectedUser = null;
        SelectedRole = null;
        SuccessMessage = null;
    }

    public void SelectRole(Role role)
    {
        CancelEdit();
        SelectedRole = SelectedRole?.Id == role.Id ? null : role;
        SelectedUser = null;
        SelectedGroup = null;
        SuccessMessage = null;
    }

    // ── Edit User ──

    public async Task StartEditUserAsync()
    {
        if (SelectedUser is null) return;
        IsEditing = true;
        ErrorMessage = null;
        SuccessMessage = null;

        EditUserName = SelectedUser.Name;

        // Alle Gruppen laden + aktuelle Zuweisungen markieren
        var allGroups = (await _groupRepo.GetAllAsync()).OrderBy(g => g.Name).ToList();
        var userGroups = await _userRepo.GetGroupsForUserAsync(SelectedUser.Id);
        var userGroupIds = userGroups.Select(g => g.Id).ToHashSet();
        EditUserGroups = allGroups.Select(g => new CheckboxItem<Group>(g, userGroupIds.Contains(g.Id))).ToList();

        // Alle Rollen laden + aktuelle Zuweisungen markieren
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

            // Gruppen aktualisieren
            var selectedGroupIds = EditUserGroups.Where(g => g.IsChecked).Select(g => g.Item.Id).ToList();
            await _userRepo.SetGroupsForUserAsync(SelectedUser.Id, selectedGroupIds);

            // Rollen aktualisieren
            var selectedRoleIds = EditUserRoles.Where(r => r.IsChecked).Select(r => r.Item.Id).ToList();
            await _userRepo.SetRolesForUserAsync(SelectedUser.Id, selectedRoleIds);

            // Listen neu laden
            await LoadTabDataAsync();

            // Aktualisierten User aus der Liste holen
            SelectedUser = Users.FirstOrDefault(u => u.Id == SelectedUser.Id);
            IsEditing = false;
            SuccessMessage = "Änderungen gespeichert.";
        }
        catch (Exception ex) { ErrorMessage = $"Fehler beim Speichern: {ex.Message}"; }
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
        catch (Exception ex) { ErrorMessage = $"Fehler beim Speichern: {ex.Message}"; }
        finally { IsSaving = false; }
    }

    // ── Helpers ──

    public void CancelEdit()
    {
        IsEditing = false;
        IsSaving = false;
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
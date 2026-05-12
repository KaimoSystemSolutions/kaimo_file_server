using System.Security.Claims;
using Kaimo_File_Server.Core.Domain.Identity;
using Kaimo_File_Server.Core.Repositories;
using Microsoft.AspNetCore.Components.Authorization;

namespace Kaimo_File_Server.Web.Components.ViewModels;

public enum AdminTab
{
    Users,
    Groups,
    Roles
}

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
    public bool IsAdmin { get; private set; }
    public bool CanManageUsers { get; private set; }
    public AdminTab ActiveTab { get; private set; } = AdminTab.Users;

    // ── Selected Items (für spätere Detail-/Bearbeitungsansicht) ──

    public User? SelectedUser { get; set; }
    public Group? SelectedGroup { get; set; }
    public Role? SelectedRole { get; set; }

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

            if (!CanManageUsers)
            {
                ErrorMessage = "Keine Berechtigung.";
                return;
            }

            await LoadTabDataAsync();
        }
        catch (Exception ex)
        {
            ErrorMessage = $"Fehler beim Laden: {ex.Message}";
        }
        finally
        {
            IsLoading = false;
        }
    }

    public async Task SwitchTabAsync(AdminTab tab)
    {
        ActiveTab = tab;
        SelectedUser = null;
        SelectedGroup = null;
        SelectedRole = null;
        ErrorMessage = null;

        try
        {
            IsLoading = true;
            await LoadTabDataAsync();
        }
        catch (Exception ex)
        {
            ErrorMessage = $"Fehler beim Laden: {ex.Message}";
        }
        finally
        {
            IsLoading = false;
        }
    }

    public void SelectUser(User user)
    {
        SelectedUser = SelectedUser?.Id == user.Id ? null : user;
    }

    public void SelectGroup(Group group)
    {
        SelectedGroup = SelectedGroup?.Id == group.Id ? null : group;
    }

    public void SelectRole(Role role)
    {
        SelectedRole = SelectedRole?.Id == role.Id ? null : role;
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
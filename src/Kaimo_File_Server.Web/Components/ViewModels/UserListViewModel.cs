using System.Security.Claims;
using Kaimo_File_Server.Core.Domain.Identity;
using Kaimo_File_Server.Core.Repositories;
using Microsoft.AspNetCore.Components.Authorization;

namespace Kaimo_File_Server.Web.Components.ViewModels;

public class UserListViewModel
{
    private readonly IUserRepository _userRepo;
    private readonly AuthenticationStateProvider _authState;

    public UserListViewModel(
        IUserRepository userRepo,
        AuthenticationStateProvider authState)
    {
        _userRepo = userRepo;
        _authState = authState;
    }

    // ── State ──

    public List<User> Users { get; private set; } = [];
    public bool IsLoading { get; private set; }
    public string? ErrorMessage { get; private set; }
    public bool IsAdmin { get; private set; }

    // ── Commands ──

    public async Task LoadAsync()
    {
        try
        {
            IsLoading = true;
            ErrorMessage = null;

            var state = await _authState.GetAuthenticationStateAsync();
            IsAdmin = state.User.IsInRole("Administrator");

            if (!IsAdmin)
            {
                ErrorMessage = "Keine Berechtigung.";
                Users = [];
                return;
            }

            var all = await _userRepo.GetAllAsync();
            Users = all.OrderBy(u => u.Name).ToList();
        }
        catch (Exception ex)
        {
            ErrorMessage = $"Fehler beim Laden der Benutzer: {ex.Message}";
            Users = [];
        }
        finally
        {
            IsLoading = false;
        }
    }
}
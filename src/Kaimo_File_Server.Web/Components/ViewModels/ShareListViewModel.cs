using System.Security.Claims;
using Kaimo_File_Server.Core.Domain;
using Kaimo_File_Server.Core.Repositories;
using Microsoft.AspNetCore.Components.Authorization;

namespace Kaimo_File_Server.Web.Components.ViewModels;

public class ShareListViewModel
{
    private readonly IShareRepository _shareRepo;
    private readonly IShareAccessRepository _accessRepo;
    private readonly AuthenticationStateProvider _authState;

    public ShareListViewModel(
        IShareRepository shareRepo,
        IShareAccessRepository accessRepo,
        AuthenticationStateProvider authState)
    {
        _shareRepo = shareRepo;
        _accessRepo = accessRepo;
        _authState = authState;
    }

    // ── State ──

    public List<ShareDefinition> Shares { get; private set; } = [];
    public bool IsLoading { get; private set; }
    public string? ErrorMessage { get; private set; }

    // ── Computed ──

    public string CurrentUserName { get; private set; } = "";

    // ── Commands ──

    public async Task LoadAsync()
    {
        try
        {
            IsLoading = true;
            ErrorMessage = null;

            var state = await _authState.GetAuthenticationStateAsync();
            CurrentUserName = state.User.FindFirst("display_name")?.Value
                           ?? state.User.Identity?.Name
                           ?? "";

            var userId = state.User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
            var allShares = await _shareRepo.GetAllEnabledAsync();

            if (userId is not null && Guid.TryParse(userId, out var uid))
            {
                // Nur Shares anzeigen, auf die der User Zugriff hat
                var accessible = new List<ShareDefinition>();
                foreach (var share in allShares)
                {
                    if (await _accessRepo.HasAccessAsync(share.Name, uid))
                        accessible.Add(share);
                }
                Shares = accessible;
            }
            else
            {
                Shares = allShares;
            }
        }
        catch (Exception ex)
        {
            ErrorMessage = $"Fehler beim Laden der Shares: {ex.Message}";
            Shares = [];
        }
        finally
        {
            IsLoading = false;
        }
    }
}

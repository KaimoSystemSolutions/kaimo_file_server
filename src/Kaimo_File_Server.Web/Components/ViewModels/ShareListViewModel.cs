using System.Security.Claims;
using Kaimo_File_Server.Core.Domain;
using Kaimo_File_Server.Core.Repositories;
using Kaimo_File_Server.Core.Storage;
using Microsoft.AspNetCore.Components.Authorization;

namespace Kaimo_File_Server.Web.Components.ViewModels;

public class ShareListViewModel
{
    private readonly IShareRepository _shareRepo;
    private readonly IShareAccessRepository _accessRepo;
    private readonly IStorageEngine _storage;
    private readonly AuthenticationStateProvider _authState;

    public ShareListViewModel(
        IShareRepository shareRepo,
        IShareAccessRepository accessRepo,
        IStorageEngine storage,
        AuthenticationStateProvider authState)
    {
        _shareRepo = shareRepo;
        _accessRepo = accessRepo;
        _storage = storage;
        _authState = authState;
    }

    // ── State ──

    public List<ShareDefinition> Shares { get; private set; } = [];
    public bool IsLoading { get; private set; }
    public string? ErrorMessage { get; private set; }

    // ── Create Share State ──

    public bool IsCreating { get; set; }
    public string NewShareName { get; set; } = "";
    public string? CreateErrorMessage { get; private set; }

    // ── Computed ──
    public string CurrentUserName { get; private set; } = "";
    public bool CanCreateShare { get; private set; }

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

            CanCreateShare = state.User.IsInRole("Administrator")
                          || state.User.IsInRole("ShareCreator");

            var userId = state.User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
            var allShares = await _shareRepo.GetAllEnabledAsync();

            if (userId is not null && Guid.TryParse(userId, out var uid))
            {
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

    public async Task<bool> CreateShareAsync()
    {
        CreateErrorMessage = null;

        var name = NewShareName.Trim();

        if (string.IsNullOrEmpty(name))
        {
            CreateErrorMessage = "Name darf nicht leer sein.";
            return false;
        }

        // Nur sichere Zeichen erlauben
        if (name.Contains("..") || name.Contains('/') || name.Contains('\\'))
        {
            CreateErrorMessage = "Ungültiger Name.";
            return false;
        }

        try
        {
            var existing = await _shareRepo.GetByNameAsync(name);
            if (existing is not null)
            {
                CreateErrorMessage = "Ein Share mit diesem Namen existiert bereits.";
                return false;
            }

            var share = new ShareDefinition(name, name);
            await _shareRepo.CreateAsync(share);

            // Verzeichnis auf der Platte anlegen
            await _storage.CreateDirectoryAsync(name);

            // Dem aktuellen User Zugriff geben
            var state = await _authState.GetAuthenticationStateAsync();
            var userId = state.User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
            if (userId is not null && Guid.TryParse(userId, out var uid))
            {
                await _accessRepo.GrantAccessAsync(share.Name, uid);
            }

            // State zurücksetzen und Liste neu laden
            NewShareName = "";
            IsCreating = false;
            await LoadAsync();
            return true;
        }
        catch (Exception ex)
        {
            CreateErrorMessage = $"Fehler: {ex.Message}";
            return false;
        }
    }
}
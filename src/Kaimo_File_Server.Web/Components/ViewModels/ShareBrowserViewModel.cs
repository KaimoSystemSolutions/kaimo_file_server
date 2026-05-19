using Kaimo_File_Server.Core.Domain;
using Kaimo_File_Server.Core.Domain.Identity;
using Kaimo_File_Server.Core.Repositories;
using Kaimo_File_Server.Core.Storage;
using Kaimo_File_Server.Infrastructure.Services;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.Extensions.Logging;
using System.Security.Claims;
using System.Text.RegularExpressions;

namespace Kaimo_File_Server.Web.Components.ViewModels;

public partial class ShareBrowserViewModel
{
    private readonly IShareRepository _shareRepo;
    private readonly IShareAccessRepository _accessRepo;
    private readonly IStorageEngine _storage;
    private readonly AuthenticationStateProvider _authState;
    private readonly ILogger<ShareBrowserViewModel> _logger;

    public ShareBrowserViewModel(
        IShareRepository shareRepo,
        IShareAccessRepository accessRepo,
        IStorageEngine storage,
        AuthenticationStateProvider authState,
        ILogger<ShareBrowserViewModel> logger)
    {
        _shareRepo = shareRepo;
        _accessRepo = accessRepo;
        _storage = storage;
        _authState = authState;
        _logger = logger;
    }

    // -- State --

    public List<ShareDefinition> Shares { get; private set; } = [];
    public bool IsLoading { get; private set; }
    public string? ErrorMessage { get; private set; }

    // -- Create Share State --

    public bool IsCreating { get; set; }
    public string NewShareName { get; set; } = "";
    public string? CreateErrorMessage { get; private set; }

    // -- Computed --
    public string CurrentUserName { get; private set; } = "";
    public bool CanCreateShare { get; private set; }

    // Regex: nur Buchstaben, Zahlen, Bindestriche, Unterstriche, Punkte
    [GeneratedRegex(@"^[a-zA-Z0-9\-_.]+$")]
    private static partial Regex SafeShareNameRegex();

    // -- Commands --

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
            _logger.LogError(ex, "Fehler beim Laden der Shares");
            ErrorMessage = "Fehler beim Laden der Shares.";
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

        if (name.Length > 64)
        {
            CreateErrorMessage = "Name darf maximal 64 Zeichen lang sein.";
            return false;
        }

        // Strenge Zeichenprüfung statt nur ".." und Slashes
        if (!SafeShareNameRegex().IsMatch(name))
        {
            CreateErrorMessage = "Nur Buchstaben, Zahlen, Bindestriche, Unterstriche und Punkte erlaubt.";
            return false;
        }

        if (name.StartsWith('.') || name.EndsWith('.'))
        {
            CreateErrorMessage = "Name darf nicht mit einem Punkt beginnen oder enden.";
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

            await _storage.CreateDirectoryAsync(name);

            var state = await _authState.GetAuthenticationStateAsync();
            var userId = state.User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
            if (userId is not null && Guid.TryParse(userId, out var uid))
            {
                await _accessRepo.GrantAccessAsync(share.Name, uid);
            }

            _logger.LogInformation("Share '{ShareName}' erstellt", name);

            NewShareName = "";
            IsCreating = false;
            await LoadAsync();
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Fehler beim Erstellen des Shares '{ShareName}'", name);
            CreateErrorMessage = "Fehler beim Erstellen des Shares.";
            return false;
        }
    }
}
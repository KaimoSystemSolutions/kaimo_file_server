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
    private readonly IStorageEngine _storage;
    private readonly AuthenticationStateProvider _authState;
    private readonly ILogger<ShareBrowserViewModel> _logger;

    public ShareBrowserViewModel(
        IShareRepository shareRepo,
        IStorageEngine storage,
        AuthenticationStateProvider authState,
        ILogger<ShareBrowserViewModel> logger)
    {
        _shareRepo = shareRepo;
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
    public bool CanManageShares { get; private set; }
    public bool IsAdmin { get; private set; }

    // -- Computed --
    public string CurrentUserName { get; private set; } = "";

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

            IsAdmin = state.User.IsInRole("Administrator")
                   || state.User.IsInRole("ShareManager");
            CanManageShares = IsAdmin;

            var userId = state.User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
            var allShares = await _shareRepo.GetAllEnabledAsync();

            if (userId is not null && Guid.TryParse(userId, out var uid))
            {
                var accessible = new List<ShareDefinition>();
                foreach (var share in allShares)
                {
                    //if (await _accessRepo.HasAccessAsync(share.Name, uid))
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

}
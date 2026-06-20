using Kaimo_File_Server.Core.Domain;
using Kaimo_File_Server.Core.Repositories;
using Kaimo_File_Server.Core.Security;
using Kaimo_File_Server.Core.Services;
using Kaimo_File_Server.Core.Storage;
using Microsoft.AspNetCore.Components.Authorization;
using System.Security.Claims;
using System.Text.RegularExpressions;
using Kaimo_File_Server.Core.Language;

namespace Kaimo_File_Server.Web.Components.ViewModels;

public partial class ShareBrowserViewModel
{
    private readonly IShareRepository _shareRepo;
    private readonly IAclService _aclService;
    private readonly IManagementAuthService _mgmtAuth;
    private readonly IUserContextFactory _userContextFactory;
    private readonly IStorageEngine _storage;
    private readonly AuthenticationStateProvider _authState;
    private readonly ILogger<ShareBrowserViewModel> _logger;

    public ShareBrowserViewModel(
        IShareRepository shareRepo,
        IAclService aclService,
        IManagementAuthService mgmtAuth,
        IUserContextFactory userContextFactory,
        IStorageEngine storage,
        AuthenticationStateProvider authState,
        ILogger<ShareBrowserViewModel> logger)
    {
        _shareRepo = shareRepo;
        _aclService = aclService;
        _mgmtAuth = mgmtAuth;
        _userContextFactory = userContextFactory;
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

            var username = state.User.Identity?.Name;
            var actor = string.IsNullOrEmpty(username)
                ? null
                : await _userContextFactory.CreateByUsernameAsync(username);

            if (actor is null)
            {
                IsAdmin = false;
                CanManageShares = false;
                Shares = [];
                return;
            }

            // Management scope (any share-management right). Global → unrestricted,
            // otherwise limited to the actor's department(s) + descendants.
            var mgmtScope = await _mgmtAuth.GetAuthorizedShareIdsAnyAsync(
                actor, ManagementPermission.ShareAdmin);

            var manageAll = mgmtScope.IsUnrestricted;
            var manageableShareIds = mgmtScope.IsUnrestricted
                ? new HashSet<Guid>()
                : mgmtScope.ScopeIds.ToHashSet();

            IsAdmin = manageAll || manageableShareIds.Count > 0;
            CanManageShares = await _mgmtAuth.HasAnyPermissionAsync(
                actor, ManagementPermission.CreateShares);

            var allShares = await _shareRepo.GetAllAsync();
            var visible = new List<ShareDefinition>(allShares.Count);

            foreach (var share in allShares)
            {
                // (A) Management view: in scope → always visible (incl. hidden/disabled).
                if (manageAll || manageableShareIds.Contains(share.Id))
                {
                    visible.Add(share);
                    continue;
                }

                // (B) Normal user view: enabled, not hidden, ListReadData on root.
                if (!share.IsEnabled || share.IsShareHidden)
                    continue;

                if (await _aclService.HasAccessAsync(
                        actor, share.Id, "", true, FilePermission.ListReadData))
                    visible.Add(share);
            }

            Shares = visible;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error loading the shares");
            ErrorMessage = Resources.Web_Error_LoadSharesFailed;
            Shares = [];
        }
        finally
        {
            IsLoading = false;
        }
    }

}
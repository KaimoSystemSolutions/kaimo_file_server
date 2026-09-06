using Kaimo_File_Server.Core.Domain;
using Kaimo_File_Server.Core.Domain.Department;
using Kaimo_File_Server.Core.Domain.Identity;
using Kaimo_File_Server.Core.Repositories;
using Kaimo_File_Server.Core.Security;
using Kaimo_File_Server.Core.Services;
using Kaimo_File_Server.Core.Services.File;
using Kaimo_File_Server.Web.DynamicHelpers;
using Microsoft.AspNetCore.Components.Authorization;
using System.Collections.Concurrent;
using System.Security.Claims;
using System.Text.RegularExpressions;
using Kaimo_File_Server.Core.Language;

namespace Kaimo_File_Server.Web.Components.ViewModels;

/// <summary>Department name plus its palette colors, for the department column chip.</summary>
public sealed record DepartmentDisplay(string Name, string Color, string Soft);

public partial class ShareBrowserViewModel
{
    private readonly IShareRepository _shareRepo;
    private readonly IAclService _aclService;
    private readonly IManagementAuthService _mgmtAuth;
    private readonly IUserContextFactory _userContextFactory;
    private readonly AuthenticationStateProvider _authState;
    private readonly ILogger<ShareBrowserViewModel> _logger;
    private readonly IFileServiceFactory _fileServiceFactory;
    private readonly IDepartmentRepository? _departmentRepo;

    public ShareBrowserViewModel(
        IShareRepository shareRepo,
        IAclService aclService,
        IManagementAuthService mgmtAuth,
        IUserContextFactory userContextFactory,
        AuthenticationStateProvider authState,
        ILogger<ShareBrowserViewModel> logger,
        IFileServiceFactory fileServiceFactory,
        IDepartmentRepository? departmentRepo = null)
    {
        _shareRepo = shareRepo;
        _aclService = aclService;
        _mgmtAuth = mgmtAuth;
        _userContextFactory = userContextFactory;
        _authState = authState;
        _logger = logger;
        _fileServiceFactory = fileServiceFactory;
        _departmentRepo = departmentRepo;
    }

    // DepartmentId → department, populated in LoadAsync only when the actor may see
    // the department column. Empty otherwise, so lookups fall back to null.
    private IReadOnlyDictionary<Guid, Department> _departments =
        new Dictionary<Guid, Department>();

    /// <summary>
    /// Whether the actor may see the department column in the share overview. Gated
    /// by the department view/edit management rights (any scope); irrelevant to
    /// normal users, useful for admins to see where each share is homed.
    /// </summary>
    public bool CanViewDepartmentColumn { get; private set; }

    /// <summary>
    /// The department a share belongs to, with its palette colors for the column
    /// chip. Returns <c>null</c> when the column is hidden or the department is
    /// unknown, so the caller can render a plain placeholder instead of a chip.
    /// </summary>
    public DepartmentDisplay? GetDepartment(ShareDefinition share)
    {
        if (!_departments.TryGetValue(share.DepartmentId, out var department))
            return null;

        var (color, soft) = DepartmentPalette.For(department.Id, department.Color);
        return new DepartmentDisplay(
            string.IsNullOrWhiteSpace(department.Name) ? "—" : department.Name, color, soft);
    }

    // -- State --

    public List<ShareDefinition> Shares { get; private set; } = [];
    public bool IsLoading { get; private set; }
    public string? ErrorMessage { get; private set; }
    public ConcurrentDictionary<Guid, long> ShareSizes { get; } = new();
    public event Action? OnStateChanged;

    // -- Create Share State --

    public bool IsCreating { get; set; }
    public string NewShareName { get; set; } = "";
    public string? CreateErrorMessage { get; private set; }
    public bool CanManageShares { get; private set; }
    public bool IsAdmin { get; private set; }

    // -- Computed --
    public string CurrentUserName { get; private set; } = "";

    public long? GetShareSize(ShareDefinition share) =>
        ShareSizes.TryGetValue(share.Id, out var size) ? size : null;

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
            ShareSizes.Clear();

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
                CanViewDepartmentColumn = false;
                _departments = new Dictionary<Guid, Department>();
                Shares = [];
                return;
            }

            // Department column: visible only to actors holding department view or
            // edit rights (any scope). Departments are loaded once so the list renders
            // name + color without a per-row lookup.
            CanViewDepartmentColumn = _departmentRepo is not null
                && await _mgmtAuth.HasAnyPermissionAsync(actor,
                    ManagementPermission.ViewDepartment | ManagementPermission.EditDepartment);
            _departments = CanViewDepartmentColumn
                ? (await _departmentRepo!.GetAllAsync())
                    .ToDictionary(department => department.Id)
                : new Dictionary<Guid, Department>();

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

            var allShares = await _shareRepo.GetAllEnabledAsync();
            //var allShares = await _shareRepo.GetAllAsync();
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
            _ = LoadShareSizesInBackgroundAsync(visible, actor);
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

    private async Task LoadShareSizesInBackgroundAsync(
        IReadOnlyCollection<ShareDefinition> shares,
        UserContext actor)
    {
        await Parallel.ForEachAsync(shares, new ParallelOptions { MaxDegreeOfParallelism = 4 },
            async (share, _) =>
            {
                try
                {
                    var service = _fileServiceFactory.CreateForShare(share.Id, share.Path);
                    ShareSizes[share.Id] = await service.GetDirectorySizeAsync("", actor);
                    OnStateChanged?.Invoke();
                }
                catch (Exception ex)
                {
                    // A failed calculation must never hide a share or block browsing it.
                    _logger.LogDebug(ex, "Could not calculate total size for share '{ShareName}'", share.Name);
                }
            });
    }

}

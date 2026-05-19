using System.Security.Claims;
using System.Text.RegularExpressions;
using Kaimo_File_Server.Core.Domain;
using Kaimo_File_Server.Core.Domain.Identity;
using Kaimo_File_Server.Core.Repositories;
using Kaimo_File_Server.Core.Services;
using Kaimo_File_Server.Core.Storage;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.Extensions.Logging;

namespace Kaimo_File_Server.Web.Components.ViewModels;

public partial class ShareListViewModel
{
    private readonly IShareRepository _shareRepo;
    private readonly IShareAccessRepository _accessRepo;
    private readonly IUserRepository _userRepo;
    private readonly IGroupRepository _groupRepo;
    private readonly IStorageEngine _storage;
    private readonly ShareLockManager _lockManager;
    private readonly AuthenticationStateProvider _authState;
    private readonly ILogger<ShareListViewModel> _logger;
    private readonly string _storagePath;

    public ShareListViewModel(
        IShareRepository shareRepo,
        IShareAccessRepository accessRepo,
        IUserRepository userRepo,
        IGroupRepository groupRepo,
        IStorageEngine storage,
        ShareLockManager lockManager,
        AuthenticationStateProvider authState,
        ILogger<ShareListViewModel> logger,
        string storagePath)
    {
        _shareRepo = shareRepo;
        _accessRepo = accessRepo;
        _userRepo = userRepo;
        _groupRepo = groupRepo;
        _storage = storage;
        _lockManager = lockManager;
        _authState = authState;
        _logger = logger;
        _storagePath = storagePath.TrimEnd('/');
    }

    // -- State --

    public List<ShareDefinition> Shares { get; private set; } = [];
    public bool IsLoading { get; private set; }
    public string? ErrorMessage { get; private set; }

    // -- Create --

    public bool IsCreating { get; set; }
    public string NewShareName { get; set; } = "";
    public string? CreateErrorMessage { get; private set; }

    // -- Edit --

    public ShareDefinition? SelectedShare { get; private set; }
    public string EditShareName { get; set; } = "";
    public string? EditErrorMessage { get; private set; }
    public string? EditSuccessMessage { get; private set; }
    public bool ShowDeleteConfirm { get; set; }
    public bool IsRenaming { get; private set; }

    // -- Access --

    public bool ShowAccessPanel { get; set; }
    public List<ShareAccessEntry> AccessEntries { get; private set; } = [];
    public List<User> AllUsers { get; private set; } = [];
    public List<Core.Domain.Identity.Group> AllGroups { get; private set; } = [];
    public string? AccessErrorMessage { get; private set; }

    // -- Computed --

    public string CurrentUserName { get; private set; } = "";
    public bool IsAdmin { get; private set; }
    public bool CanCreateShare { get; private set; }

    [GeneratedRegex(@"^[a-zA-Z0-9\-_.]+$")]
    private static partial Regex SafeShareNameRegex();

    // -- Load --

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
            CanCreateShare = IsAdmin;

            var userId = state.User.FindFirst(ClaimTypes.NameIdentifier)?.Value;

            // Admins sehen alle Shares (auch deaktivierte), normale User nur aktivierte
            var allShares = IsAdmin
                ? await _shareRepo.GetAllAsync()
                : await _shareRepo.GetAllEnabledAsync();

            if (userId is not null && Guid.TryParse(userId, out var uid))
            {
                if (IsAdmin)
                {
                    // Admins sehen alles
                    Shares = allShares;
                }
                else
                {
                    var accessible = new List<ShareDefinition>();
                    foreach (var share in allShares)
                    {
                        if (await _accessRepo.HasAccessAsync(share.Name, uid))
                            accessible.Add(share);
                    }
                    Shares = accessible;
                }
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

    // -- Validation --

    private bool ValidateShareName(string name, out string? error)
    {
        error = null;

        if (string.IsNullOrEmpty(name))
        { error = "Name darf nicht leer sein."; return false; }

        if (name.Length > 64)
        { error = "Name darf maximal 64 Zeichen lang sein."; return false; }

        if (!SafeShareNameRegex().IsMatch(name))
        { error = "Nur Buchstaben, Zahlen, Bindestriche, Unterstriche und Punkte erlaubt."; return false; }

        if (name.StartsWith('.') || name.EndsWith('.'))
        { error = "Name darf nicht mit einem Punkt beginnen oder enden."; return false; }

        return true;
    }

    private string BuildSharePath(string name) => $"{_storagePath}/{name}";

    // -- Create --

    public async Task<bool> CreateShareAsync()
    {
        CreateErrorMessage = null;
        var name = NewShareName.Trim();

        if (!ValidateShareName(name, out var error))
        { CreateErrorMessage = error; return false; }

        try
        {
            var existing = await _shareRepo.GetByNameAsync(name);
            if (existing is not null)
            { CreateErrorMessage = "Ein Share mit diesem Namen existiert bereits."; return false; }

            var share = new ShareDefinition(name, BuildSharePath(name));
            await _shareRepo.CreateAsync(share);
            await _storage.CreateDirectoryAsync(name);

            var state = await _authState.GetAuthenticationStateAsync();
            var userId = state.User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
            if (userId is not null && Guid.TryParse(userId, out var uid))
            {
                // 1. Share-Sichtbarkeit
                await _accessRepo.GrantAccessAsync(share.Name, uid);

                // 2. Root-FileMetadata für den Share anlegen + Owner-ACL
                await _accessRepo.EnsureShareRootAclAsync(share.Id, uid);
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

    // -- Select / Deselect --

    public void SelectShare(ShareDefinition share)
    {
        if (SelectedShare?.Id == share.Id)
        {
            DeselectShare();
            return;
        }

        SelectedShare = share;
        EditShareName = share.Name;
        EditErrorMessage = null;
        EditSuccessMessage = null;
        ShowDeleteConfirm = false;
        ShowAccessPanel = false;
        IsCreating = false;
    }

    public void DeselectShare()
    {
        SelectedShare = null;
        EditShareName = "";
        EditErrorMessage = null;
        EditSuccessMessage = null;
        ShowDeleteConfirm = false;
        ShowAccessPanel = false;
    }

    // -- Rename (mit Lock) --

    public async Task<bool> RenameShareAsync()
    {
        if (SelectedShare is null) return false;

        EditErrorMessage = null;
        EditSuccessMessage = null;
        var newName = EditShareName.Trim();
        var oldName = SelectedShare.Name;

        if (newName == oldName)
        { EditErrorMessage = "Der Name ist unverändert."; return false; }

        if (!ValidateShareName(newName, out var error))
        { EditErrorMessage = error; return false; }

        var existingNew = await _shareRepo.GetByNameAsync(newName);
        if (existingNew is not null)
        { EditErrorMessage = "Ein Share mit diesem Namen existiert bereits."; return false; }

        var shareLock = _lockManager.GetLock(oldName);

        IsRenaming = true;
        try
        {
            // Lock holen – wartet bis alle laufenden Ops fertig sind
            if (!await shareLock.WaitAsync(TimeSpan.FromSeconds(30)))
            {
                EditErrorMessage = "Share ist gerade in Benutzung. Bitte versuche es erneut.";
                return false;
            }

            try
            {
                // 1. Physischen Ordner umbenennen
                var oldFullPath = Path.Combine(_storagePath, oldName);
                var newFullPath = Path.Combine(_storagePath, newName);

                if (Directory.Exists(oldFullPath))
                    Directory.Move(oldFullPath, newFullPath);

                // 2. ShareDefinition in DB updaten
                SelectedShare.Name = newName;
                SelectedShare.Path = BuildSharePath(newName);
                await _shareRepo.UpdateAsync(SelectedShare);

                // 3. Alle AccessEntries migrieren
                await _accessRepo.UpdateShareNameAsync(oldName, newName);

                // 4. Lock-Key umbenennen
                _lockManager.RenameLock(oldName, newName);

                _logger.LogInformation("Share '{OldName}' umbenannt zu '{NewName}'", oldName, newName);

                EditSuccessMessage = $"Share umbenannt zu '{newName}'.";
                EditShareName = newName;
                await LoadAsync();

                // Re-select mit neuen Daten
                var updated = Shares.FirstOrDefault(s => s.Id == SelectedShare.Id);
                if (updated is not null)
                    SelectedShare = updated;

                return true;
            }
            finally
            {
                shareLock.Release();
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Fehler beim Umbenennen des Shares '{OldName}' -> '{NewName}'", oldName, newName);
            EditErrorMessage = "Fehler beim Umbenennen. Bitte prüfe die Logs.";
            return false;
        }
        finally
        {
            IsRenaming = false;
        }
    }

    // -- Toggle Enabled --

    public async Task<bool> ToggleShareEnabledAsync()
    {
        if (SelectedShare is null) return false;

        EditErrorMessage = null;
        EditSuccessMessage = null;

        try
        {
            SelectedShare.IsEnabled = !SelectedShare.IsEnabled;
            await _shareRepo.UpdateAsync(SelectedShare);

            var status = SelectedShare.IsEnabled ? "aktiviert" : "deaktiviert";
            _logger.LogInformation("Share '{ShareName}' {Status}", SelectedShare.Name, status);
            EditSuccessMessage = $"Share {status}.";

            await LoadAsync();

            // Re-select
            var updated = Shares.FirstOrDefault(s => s.Id == SelectedShare.Id);
            if (updated is not null)
                SelectedShare = updated;

            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Fehler beim Ändern des Share-Status");
            EditErrorMessage = "Fehler beim Ändern des Status.";
            return false;
        }
    }

    // -- Delete --

    public async Task<bool> DeleteShareAsync()
    {
        if (SelectedShare is null) return false;

        EditErrorMessage = null;

        try
        {
            var name = SelectedShare.Name;
            await _shareRepo.DeleteAsync(SelectedShare.Id);
            _lockManager.RemoveLock(name);

            _logger.LogInformation("Share '{ShareName}' gelöscht", name);

            DeselectShare();
            await LoadAsync();
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Fehler beim Löschen des Shares");
            EditErrorMessage = "Fehler beim Löschen.";
            return false;
        }
    }

    // -- Access Management --

    public async Task LoadAccessAsync()
    {
        if (SelectedShare is null) return;

        AccessErrorMessage = null;

        try
        {
            ShowAccessPanel = true;
            AccessEntries = await _accessRepo.GetByShareAsync(SelectedShare.Name);
            AllUsers = (await _userRepo.GetAllAsync()).ToList();
            AllGroups = (await _groupRepo.GetAllAsync()).ToList();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Fehler beim Laden der Zugriffsrechte");
            AccessErrorMessage = "Fehler beim Laden der Zugriffsrechte.";
        }
    }

    public async Task GrantAccessAsync(Guid principalId)
    {
        if (SelectedShare is null) return;
        AccessErrorMessage = null;

        try
        {
            await _accessRepo.GrantAccessAsync(SelectedShare.Name, principalId);
            AccessEntries = await _accessRepo.GetByShareAsync(SelectedShare.Name);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Fehler beim Gewähren des Zugriffs");
            AccessErrorMessage = "Fehler beim Gewähren des Zugriffs.";
        }
    }

    public async Task RevokeAccessAsync(Guid principalId)
    {
        if (SelectedShare is null) return;
        AccessErrorMessage = null;

        try
        {
            await _accessRepo.RevokeAccessAsync(SelectedShare.Name, principalId);
            AccessEntries = await _accessRepo.GetByShareAsync(SelectedShare.Name);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Fehler beim Entziehen des Zugriffs");
            AccessErrorMessage = "Fehler beim Entziehen des Zugriffs.";
        }
    }

    public string GetPrincipalDisplayName(Guid principalId)
    {
        var user = AllUsers.FirstOrDefault(u => u.Id == principalId);
        if (user is not null) return user.Name ?? user.Username;

        var group = AllGroups.FirstOrDefault(g => g.Id == principalId);
        if (group is not null) return group.Name;

        return principalId.ToString()[..8] + "…";
    }

    public string GetPrincipalType(Guid principalId)
    {
        if (AllUsers.Any(u => u.Id == principalId)) return "user";
        if (AllGroups.Any(g => g.Id == principalId)) return "group";
        return "unknown";
    }

    public bool HasAccess(Guid principalId)
        => AccessEntries.Any(e => e.PrincipalId == principalId);
}